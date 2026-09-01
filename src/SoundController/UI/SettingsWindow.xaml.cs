using Microsoft.Extensions.Logging;
using SoundController.Autostart;
using SoundController.Config;
using SoundController.Core;
using SoundController.Orchestration;
using SoundController.Sonar;
using SoundController.Tray;
using SoundController.WindowsAudio;
using System.Windows;
using System.Windows.Controls;

namespace SoundController.UI;

/// <summary>
/// Settings window code-behind. Loads device lists from the services on open
/// (off the UI thread), edits settings through plain fields, and saves through
/// <see cref="SettingsService"/>. Kept deliberately thin: no state lives here
/// after close.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Combo box option wrapper; empty ID means "not locked".</summary>
    public sealed record ComboItem(string Id, string Display);

    private static readonly ComboItem NotLockedItem = new(string.Empty, "(not locked)");

    private readonly SettingsService _settingsService;
    private readonly IWindowsAudioService _windowsAudio;
    private readonly ISonarService _sonar;
    private readonly RestoreCoordinator _coordinator;
    private readonly IAutostartService _autostart;
    private readonly ILogger<SettingsWindow> _logger;

    // While true, SelectionChanged handlers do not mark combos as
    // user-touched. Programmatic selection (loading saved values, refreshing
    // after capture) must not look like user intent - otherwise an untouched
    // combo could wipe its saved lock on Save/Apply.
    private bool _suppressSelectionTracking;

    // Which profile the combos currently edit. Null until the first load
    // follows the active profile; afterwards only the radio pair changes it.
    private string? _editTargetProfileId;

    public SettingsWindow(
        SettingsService settingsService,
        IWindowsAudioService windowsAudio,
        ISonarService sonar,
        RestoreCoordinator coordinator,
        IAutostartService autostart,
        ILogger<SettingsWindow> logger)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _windowsAudio = windowsAudio;
        _sonar = sonar;
        _coordinator = coordinator;
        _autostart = autostart;
        _logger = logger;

        foreach (var combo in LockedCombos())
        {
            combo.SelectionChanged += OnLockedComboSelectionChanged;
        }

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _coordinator.StatusChanged -= OnCoordinatorStatusChanged;
            _coordinator.ActiveProfileChanged -= OnCoordinatorActiveProfileChanged;
            _settingsService.Saved -= OnSettingsSaved;
        };
        _coordinator.StatusChanged += OnCoordinatorStatusChanged;
        _coordinator.ActiveProfileChanged += OnCoordinatorActiveProfileChanged;
        _settingsService.Saved += OnSettingsSaved;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(preserveSelections: false).ConfigureAwait(true);
    }

    private async Task RefreshAsync(bool preserveSelections)
    {
        try
        {
            SetBusy(true, "Loading devices...");

            var load = await _settingsService.LoadAsync().ConfigureAwait(true);
            var settings = load.Settings ?? new AppSettings();
            AutoRestoreCheckBox.IsChecked = settings.AutoRestoreEnabled;
            StartWithWindowsCheckBox.IsChecked = _autostart.IsEnabled();

            // First load follows the active profile; later loads keep whatever
            // edit target the radio pair selected.
            _editTargetProfileId ??= ProfileIds.IsKnown(settings.ActiveProfileId)
                ? settings.ActiveProfileId
                : ProfileIds.Headphones;

            var loadPlayback = _windowsAudio.ListEndpointsAsync(AudioDirection.Render, CancellationToken.None);
            var loadRecording = _windowsAudio.ListEndpointsAsync(AudioDirection.Capture, CancellationToken.None);
            var loadSonar = _sonar.ListDevicesAsync(CancellationToken.None);
            await Task.WhenAll(loadPlayback, loadRecording, loadSonar).ConfigureAwait(true);

            var playbackOptions = BuildWindowsOptions(loadPlayback.Result);
            var recordingOptions = BuildWindowsOptions(loadRecording.Result);
            var sonarRender = BuildSonarOptions(loadSonar.Result, AudioDirectionHint.Render);
            var sonarCapture = BuildSonarOptions(loadSonar.Result, AudioDirectionHint.Capture);

            // Everything below is programmatic state loading; user touches
            // made before this point are intentionally discarded (a refresh
            // re-reads saved state), and programmatic changes must not mark
            // slots as touched.
            _suppressSelectionTracking = true;
            List<string> missing;
            try
            {
                var editTarget = settings.Profiles.TryGetValue(_editTargetProfileId, out var targetProfile)
                    ? targetProfile
                    : null;

                string activeName = settings.ActiveProfile?.Name is { Length: > 0 } activeLabel
                    ? activeLabel
                    : ProfileIds.DisplayNameFor(settings.ActiveProfileId);
                HeadphonesRadioButton.IsChecked = _editTargetProfileId == ProfileIds.Headphones;
                SpeakersRadioButton.IsChecked = _editTargetProfileId == ProfileIds.Speakers;
                ActiveProfileText.Text = $"Active: {activeName}";
                MakeActiveButton.IsEnabled =
                    !string.Equals(_editTargetProfileId, settings.ActiveProfileId, StringComparison.Ordinal);

                SetCombo(PlaybackDefaultComboBox, playbackOptions);
                SetCombo(PlaybackCommsComboBox, playbackOptions);
                SetCombo(RecordingDefaultComboBox, recordingOptions);
                SetCombo(RecordingCommsComboBox, recordingOptions);
                SetCombo(GameComboBox, sonarRender);
                SetCombo(ChatComboBox, sonarRender);
                SetCombo(MediaComboBox, sonarRender);
                SetCombo(AuxComboBox, sonarRender);
                SetCombo(MicComboBox, sonarCapture);

                missing = new List<string>();
                if (editTarget?.Windows is not null)
                {
                    var windows = editTarget.Windows;
                    TrackMissing(missing, Select(PlaybackDefaultComboBox, windows.PlaybackConsoleId ?? windows.PlaybackMultimediaId), "playback default");
                    TrackMissing(missing, Select(PlaybackCommsComboBox, windows.PlaybackCommunicationsId), "playback communications");
                    TrackMissing(missing, Select(RecordingDefaultComboBox, windows.RecordingConsoleId ?? windows.RecordingMultimediaId), "recording default");
                    TrackMissing(missing, Select(RecordingCommsComboBox, windows.RecordingCommunicationsId), "recording communications");
                }

                if (editTarget?.Sonar is not null)
                {
                    TrackMissing(missing, Select(GameComboBox, ChannelId(editTarget, "Game")), "Sonar Game");
                    TrackMissing(missing, Select(ChatComboBox, ChannelId(editTarget, "Chat")), "Sonar Chat");
                    TrackMissing(missing, Select(MediaComboBox, ChannelId(editTarget, "Media")), "Sonar Media");
                    TrackMissing(missing, Select(AuxComboBox, ChannelId(editTarget, "Aux")), "Sonar Aux");
                    TrackMissing(missing, Select(MicComboBox, ChannelId(editTarget, "Mic")), "Sonar Mic");
                }
            }
            finally
            {
                _suppressSelectionTracking = false;
            }

            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "Saved devices not in the current device list (locks kept): {Missing}",
                    string.Join("; ", missing));
            }

            string readyMessage = (preserveSelections ? "Refreshed from current state." : "Ready.") +
                $" Editing {ProfileIds.DisplayNameFor(_editTargetProfileId)} profile.";
            StatusText.Text = missing.Count > 0
                ? readyMessage + " Saved device(s) not in the current list - locks kept: " + string.Join(", ", missing) + "."
                : readyMessage;
        }
        catch (SonarUnsupportedModeException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings window refresh failed");
            StatusText.Text = "Loading devices failed - saved locks are kept for unchanged slots. See logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void TrackMissing(List<string> missing, bool selected, string slotLabel)
    {
        // selected=false only when a saved device exists but was not offered
        // by the current device list (e.g. it is unplugged right now).
        if (!selected)
        {
            missing.Add($"({slotLabel})");
        }
    }

    private static string? ChannelId(ProfileSettings? profile, string channel) =>
        profile?.Sonar?.Channels.TryGetValue(channel, out var id) == true ? id : null;

    private static List<ComboItem> BuildWindowsOptions(IReadOnlyList<AudioEndpointOption> endpoints) =>
        new List<ComboItem> { NotLockedItem }.Concat(endpoints.Select(d => new ComboItem(d.Id, d.Name))).ToList();

    private static List<ComboItem> BuildSonarOptions(IReadOnlyList<SonarDeviceOption> devices, AudioDirectionHint flow) =>
        new List<ComboItem> { NotLockedItem }
            .Concat(devices
                .Where(d => d.Flow == flow)
                .Select(d => new ComboItem(d.Id, d.IsSonarVirtual ? $"{d.Name} (Sonar virtual)" : d.Name)))
            .ToList();

    private static void SetCombo(ComboBox comboBox, List<ComboItem> options)
    {
        comboBox.ItemsSource = options;
        comboBox.DisplayMemberPath = nameof(ComboItem.Display);
        comboBox.SelectedValuePath = nameof(ComboItem.Id);
        // A refresh resets the slot to its saved state: any earlier user
        // touch is discarded.
        comboBox.Tag = null;
        // Windows UI convention: nothing locked until the user picks a device.
        comboBox.SelectedValue = string.Empty;
    }

    /// <summary>
    /// Selects the saved device in the combo. Returns false only when a
    /// non-empty saved device was not offered by the current device list
    /// (typically because it is unplugged right now); an empty/null saved
    /// value is simply "not locked", not an error.
    /// </summary>
    private static bool Select(ComboBox comboBox, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return true;
        }

        if (!comboBox.Items.Cast<ComboItem>().Any(i => i.Id == deviceId))
        {
            return false;
        }

        comboBox.SelectedValue = deviceId;
        return true;
    }

    private async void OnCaptureClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Capturing current state...");
            // Capture always writes the live devices into the ACTIVE profile
            // (the one auto-restore enforces); the coordinator's status names it.
            await _coordinator.CaptureCurrentStateAsync().ConfigureAwait(true);
            await RefreshAsync(preserveSelections: true).ConfigureAwait(true);
            StatusText.Text = "Current devices captured into the active profile.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture failed from settings window");
            StatusText.Text = "Capture failed - see logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnAutoRestoreToggleClicked(object sender, RoutedEventArgs e)
    {
        // Click only fires on user interaction, so IsChecked here is the
        // freshly toggled value; programmatic updates below cannot re-enter.
        bool enable = AutoRestoreCheckBox.IsChecked == true;
        try
        {
            SetBusy(true, enable ? "Enabling auto-restore..." : "Disabling auto-restore...");

            var load = await _settingsService.LoadAsync().ConfigureAwait(true);
            var settings = load.Settings ?? new AppSettings();
            settings.AutoRestoreEnabled = enable;
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
            _logger.LogInformation("Auto-restore toggled to {Enabled} from settings window", enable);

            if (enable)
            {
                // Re-check immediately so the user sees protection resume.
                _coordinator.RequestRestore("auto-restore enabled");
                StatusText.Text = "Auto-restore enabled.";
            }
            else
            {
                StatusText.Text = "Auto-restore disabled.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling auto-restore failed from settings window");
            StatusText.Text = "Auto-restore change failed - see logs.";
            AutoRestoreCheckBox.IsChecked = !enable;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartWithWindowsToggleClicked(object sender, RoutedEventArgs e)
    {
        bool enable = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            SetBusy(true, enable ? "Enabling start with Windows..." : "Disabling start with Windows...");

            // The registry is the source of truth; settings only mirror it.
            _autostart.SetEnabled(enable);
            var load = await _settingsService.LoadAsync().ConfigureAwait(true);
            var settings = load.Settings ?? new AppSettings();
            settings.StartWithWindows = enable;
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
            StatusText.Text = enable ? "Start with Windows enabled." : "Start with Windows disabled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling autostart failed from settings window");
            StatusText.Text = "Start with Windows change failed - see logs.";
            // Reflect what actually happened instead of the failed intent.
            StartWithWindowsCheckBox.IsChecked = _autostart.IsEnabled();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        // A toggle saved anywhere (tray, coordinator, this window) keeps the
        // checkboxes current while the window is open. Programmatic IsChecked
        // updates do not raise Click, so this cannot re-trigger the handlers.
        Dispatcher.BeginInvoke(() =>
        {
            AutoRestoreCheckBox.IsChecked = settings.AutoRestoreEnabled;
            StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        });
    }

    private async void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Applying locked state...");
            // Save first so "apply now" reflects what the window shows.
            await SaveSettingsAsync().ConfigureAwait(true);
            await _coordinator.RestoreNowAsync().ConfigureAwait(true);
            StatusText.Text = "Locked state applied.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply failed from settings window");
            StatusText.Text = "Apply failed - see logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Saving...");
            await SaveSettingsAsync().ConfigureAwait(true);
            StatusText.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save failed from settings window");
            StatusText.Text = "Save failed - see logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveSettingsAsync()
    {
        var load = await _settingsService.LoadAsync().ConfigureAwait(true);
        var settings = load.Settings ?? new AppSettings();

        // Auto-restore and Start with Windows are NOT written here: both
        // checkboxes apply immediately on click (and sync through
        // SettingsService.Saved), so reading them at Save time would replay
        // stale values over changes made elsewhere - the tray/settings
        // disconnect. Save only concerns the device lists.

        // Merge semantics (LockedSlotMerge): slots the user did not touch
        // keep their previous locked value, so a missing device in the list
        // can never silently wipe a lock. "(not locked)" only clears a slot
        // when the user deliberately picked it. Previous values come from the
        // profile being edited, so each configuration merges independently.
        var editTarget = settings.GetOrCreateProfile(_editTargetProfileId ?? ProfileIds.Headphones);

        editTarget.Windows = new WindowsDefaultsSettings
        {
            // "Default" writes both Console and Multimedia; see XAML comment.
            PlaybackConsoleId = ResolveSlot(editTarget.Windows?.PlaybackConsoleId, PlaybackDefaultComboBox),
            PlaybackMultimediaId = ResolveSlot(editTarget.Windows?.PlaybackMultimediaId, PlaybackDefaultComboBox),
            PlaybackCommunicationsId = ResolveSlot(editTarget.Windows?.PlaybackCommunicationsId, PlaybackCommsComboBox),
            RecordingConsoleId = ResolveSlot(editTarget.Windows?.RecordingConsoleId, RecordingDefaultComboBox),
            RecordingMultimediaId = ResolveSlot(editTarget.Windows?.RecordingMultimediaId, RecordingDefaultComboBox),
            RecordingCommunicationsId = ResolveSlot(editTarget.Windows?.RecordingCommunicationsId, RecordingCommsComboBox),
        };

        editTarget.Sonar = new SonarDefaultsSettings
        {
            Channels = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Game"] = ResolveSlot(ChannelId(editTarget, "Game"), GameComboBox),
                ["Chat"] = ResolveSlot(ChannelId(editTarget, "Chat"), ChatComboBox),
                ["Media"] = ResolveSlot(ChannelId(editTarget, "Media"), MediaComboBox),
                ["Aux"] = ResolveSlot(ChannelId(editTarget, "Aux"), AuxComboBox),
                ["Mic"] = ResolveSlot(ChannelId(editTarget, "Mic"), MicComboBox),
            },
        };

        // Refresh the display-name cache from whatever the combos show so
        // logs and tray messages stay readable.
        foreach (var combo in new[] { PlaybackDefaultComboBox, PlaybackCommsComboBox, RecordingDefaultComboBox, RecordingCommsComboBox, GameComboBox, ChatComboBox, MediaComboBox, AuxComboBox, MicComboBox })
        {
            if (combo.ItemsSource is IEnumerable<ComboItem> items)
            {
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.Id))
                    {
                        settings.DeviceNames[item.Id] = item.Display;
                    }
                }
            }
        }

        await _settingsService.SaveAsync(settings).ConfigureAwait(true);
    }

    private static string? SelectedId(ComboBox comboBox) => comboBox.SelectedValue as string;

    /// <summary>Resolves one locked slot: user-touched combos take the UI value, untouched ones keep the saved value.</summary>
    private static string? ResolveSlot(string? previous, ComboBox combo) =>
        LockedSlotMerge.Resolve(previous, SelectedId(combo), combo.Tag is bool touched && touched);

    /// <summary>All combos backed by locked-device slots, in save order.</summary>
    private ComboBox[] LockedCombos() => new[]
    {
        PlaybackDefaultComboBox, PlaybackCommsComboBox, RecordingDefaultComboBox, RecordingCommsComboBox,
        GameComboBox, ChatComboBox, MediaComboBox, AuxComboBox, MicComboBox,
    };

    private void OnLockedComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Programmatic loading (SetCombo/Select during RefreshAsync) is
        // suppressed; only real user changes mark the slot as touched.
        if (!_suppressSelectionTracking && sender is ComboBox combo)
        {
            combo.Tag = true;
        }
    }

    private async void OnProfileChecked(object sender, RoutedEventArgs e)
    {
        // Programmatic radio state during RefreshAsync must not re-trigger a
        // refresh; also ignore events before the window is usable.
        if (_suppressSelectionTracking || !IsLoaded)
        {
            return;
        }

        string profileId = ReferenceEquals(sender, HeadphonesRadioButton)
            ? ProfileIds.Headphones
            : ProfileIds.Speakers;
        if (profileId == _editTargetProfileId)
        {
            return;
        }

        // Switching the edit target re-reads saved state for that profile;
        // unsaved combo touches are discarded, same as any other refresh.
        _editTargetProfileId = profileId;
        await RefreshAsync(preserveSelections: false).ConfigureAwait(true);
    }

    private async void OnMakeActiveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Switching active configuration...");
            // Save first so the switch applies what the window shows,
            // mirroring the "Apply now" flow.
            await SaveSettingsAsync().ConfigureAwait(true);
            await _coordinator
                .ActivateProfileAsync(_editTargetProfileId ?? ProfileIds.Headphones)
                .ConfigureAwait(true);
            // The coordinator reports the apply outcome through StatusChanged
            // and raises ActiveProfileChanged, which refreshes the indicator
            // and this button's enabled state.
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the click; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Make active failed from settings window");
            StatusText.Text = "Switching configuration failed - see logs.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCoordinatorActiveProfileChanged(string profileId)
    {
        // Fired from background continuations; marshal the visual update.
        Dispatcher.BeginInvoke(() =>
        {
            ActiveProfileText.Text = $"Active: {ProfileIds.DisplayNameFor(profileId)}";
            MakeActiveButton.IsEnabled =
                !string.Equals(_editTargetProfileId, profileId, StringComparison.Ordinal);
        });
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string? message = null)
    {
        CaptureButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        AutoRestoreCheckBox.IsEnabled = !busy;
        StartWithWindowsCheckBox.IsEnabled = !busy;
        if (message is not null)
        {
            StatusText.Text = message;
        }
    }

    private void OnCoordinatorStatusChanged(ProtectionStatus status)
    {
        // Status arrives from background threads; BeginInvoke marshals it.
        Dispatcher.BeginInvoke(() => StatusText.Text = status.Message);
    }
}
