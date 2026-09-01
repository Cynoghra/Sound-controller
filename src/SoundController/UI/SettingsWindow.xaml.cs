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
        Closed += (_, _) => _coordinator.StatusChanged -= OnCoordinatorStatusChanged;
        _coordinator.StatusChanged += OnCoordinatorStatusChanged;
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
                if (settings.Windows is not null)
                {
                    TrackMissing(missing, settings, Select(PlaybackDefaultComboBox, settings.Windows.PlaybackConsoleId ?? settings.Windows.PlaybackMultimediaId), "playback default");
                    TrackMissing(missing, settings, Select(PlaybackCommsComboBox, settings.Windows.PlaybackCommunicationsId), "playback communications");
                    TrackMissing(missing, settings, Select(RecordingDefaultComboBox, settings.Windows.RecordingConsoleId ?? settings.Windows.RecordingMultimediaId), "recording default");
                    TrackMissing(missing, settings, Select(RecordingCommsComboBox, settings.Windows.RecordingCommunicationsId), "recording communications");
                }

                if (settings.Sonar is not null)
                {
                    TrackMissing(missing, settings, Select(GameComboBox, ChannelId(settings, "Game")), "Sonar Game");
                    TrackMissing(missing, settings, Select(ChatComboBox, ChannelId(settings, "Chat")), "Sonar Chat");
                    TrackMissing(missing, settings, Select(MediaComboBox, ChannelId(settings, "Media")), "Sonar Media");
                    TrackMissing(missing, settings, Select(AuxComboBox, ChannelId(settings, "Aux")), "Sonar Aux");
                    TrackMissing(missing, settings, Select(MicComboBox, ChannelId(settings, "Mic")), "Sonar Mic");
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

            string readyMessage = preserveSelections ? "Refreshed from current state." : "Ready.";
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

    private void TrackMissing(List<string> missing, AppSettings settings, bool selected, string slotLabel)
    {
        // selected=false only when a saved device exists but was not offered
        // by the current device list (e.g. it is unplugged right now).
        if (!selected)
        {
            missing.Add($"({slotLabel})");
        }
    }

    private static string? ChannelId(AppSettings settings, string channel) =>
        settings.Sonar?.Channels.TryGetValue(channel, out var id) == true ? id : null;

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
            await _coordinator.CaptureCurrentStateAsync().ConfigureAwait(true);
            await RefreshAsync(preserveSelections: true).ConfigureAwait(true);
            StatusText.Text = "Current devices captured as locked state.";
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

        settings.AutoRestoreEnabled = AutoRestoreCheckBox.IsChecked == true;

        bool wantAutostart = StartWithWindowsCheckBox.IsChecked == true;
        if (wantAutostart != _autostart.IsEnabled())
        {
            _autostart.SetEnabled(wantAutostart);
        }

        // Merge semantics (LockedSlotMerge): slots the user did not touch
        // keep their previous locked value, so a missing device in the list
        // can never silently wipe a lock. "(not locked)" only clears a slot
        // when the user deliberately picked it.
        settings.Windows = new WindowsDefaultsSettings
        {
            // "Default" writes both Console and Multimedia; see XAML comment.
            PlaybackConsoleId = ResolveSlot(settings.Windows?.PlaybackConsoleId, PlaybackDefaultComboBox),
            PlaybackMultimediaId = ResolveSlot(settings.Windows?.PlaybackMultimediaId, PlaybackDefaultComboBox),
            PlaybackCommunicationsId = ResolveSlot(settings.Windows?.PlaybackCommunicationsId, PlaybackCommsComboBox),
            RecordingConsoleId = ResolveSlot(settings.Windows?.RecordingConsoleId, RecordingDefaultComboBox),
            RecordingMultimediaId = ResolveSlot(settings.Windows?.RecordingMultimediaId, RecordingDefaultComboBox),
            RecordingCommunicationsId = ResolveSlot(settings.Windows?.RecordingCommunicationsId, RecordingCommsComboBox),
        };

        settings.Sonar = new SonarDefaultsSettings
        {
            Channels = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Game"] = ResolveSlot(ChannelId(settings, "Game"), GameComboBox),
                ["Chat"] = ResolveSlot(ChannelId(settings, "Chat"), ChatComboBox),
                ["Media"] = ResolveSlot(ChannelId(settings, "Media"), MediaComboBox),
                ["Aux"] = ResolveSlot(ChannelId(settings, "Aux"), AuxComboBox),
                ["Mic"] = ResolveSlot(ChannelId(settings, "Mic"), MicComboBox),
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
    private IEnumerable<ComboBox> LockedCombos() => new[]
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string? message = null)
    {
        CaptureButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
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
