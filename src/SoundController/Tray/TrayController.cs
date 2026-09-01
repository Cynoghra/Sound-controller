using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using SoundController.Autostart;
using SoundController.Config;
using SoundController.Orchestration;
using SoundController.Sonar;

namespace SoundController.Tray;

/// <summary>
/// Owns the tray icon and its context menu. View-adjacent by design: it only
/// translates user intent into service calls and status into visuals. All
/// slow work happens in services; handlers here run on the UI thread and
/// marshal results with async/await.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly RestoreCoordinator _coordinator;
    private readonly SettingsService _settingsService;
    private readonly IAutostartService _autostart;
    private readonly ISonarService _sonar;
    private readonly ILogger<TrayController> _logger;

    private readonly TaskbarIcon _icon = new();
    private MenuItem _statusItem = null!;
    private MenuItem _autoRestoreItem = null!;
    private MenuItem _autostartItem = null!;
    private readonly Dictionary<string, MenuItem> _profileItems = new(StringComparer.Ordinal);
    private string _activeProfileId = ProfileIds.Headphones;

    // Latest saved settings snapshot, used to keep menu checkmarks current
    // without disk I/O on the UI thread; seeded at Initialize and refreshed
    // through SettingsService.Saved.
    private AppSettings? _lastSettings;
    private readonly List<IntPtr> _liveIconHandles = new();

    /// <summary>Raised when the user asks for the settings window.</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>Raised when the user asks to exit the application.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Raised after the user confirmed full removal (autostart entry +
    /// app data folder). App decides the shutdown/cleanup order.
    /// </summary>
    public event Action? CleanupRequested;

    public TrayController(
        RestoreCoordinator coordinator,
        SettingsService settingsService,
        IAutostartService autostart,
        ISonarService sonar,
        ILogger<TrayController> logger)
    {
        _coordinator = coordinator;
        _settingsService = settingsService;
        _autostart = autostart;
        _sonar = sonar;
        _logger = logger;

        _coordinator.StatusChanged += OnStatusChanged;
        _coordinator.ActiveProfileChanged += OnActiveProfileChanged;
        _settingsService.Saved += OnSettingsSaved;
    }

    public void Initialize(AppSettings settings)
    {
        _icon.ToolTipText = "SoundController - starting...";
        SetIcon(ProtectionState.Restoring);
        _lastSettings = settings;

        _statusItem = new MenuItem { Header = "Starting...", IsEnabled = false };
        _autoRestoreItem = new MenuItem
        {
            Header = "Auto-restore enabled",
            IsCheckable = true,
            IsChecked = settings.AutoRestoreEnabled,
        };
        _autoRestoreItem.Click += OnAutoRestoreToggled;

        _autostartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            // The registry is the source of truth; reflect its actual state.
            IsChecked = _autostart.IsEnabled(),
        };
        _autostartItem.Click += OnAutostartToggled;

        // Radio-style profile toggle: exactly one of the two configurations is
        // active. Mutual exclusion is maintained manually (SyncProfileChecks)
        // so behavior does not depend on WPF group semantics. The header is a
        // display label; the stable profile ID rides in Tag.
        _activeProfileId = settings.ActiveProfileId;
        _profileItems[ProfileIds.Headphones] = CreateProfileItem(ProfileIds.Headphones);
        _profileItems[ProfileIds.Speakers] = CreateProfileItem(ProfileIds.Speakers);
        SyncProfileChecks();

        var captureItem = CreateItem("Capture current setup", OnCaptureClicked);
        var applyItem = CreateItem("Apply saved setup now", OnApplyClicked);
        var settingsItem = CreateItem("Settings...", OnSettingsClicked);
        var logsItem = CreateItem("Open logs folder", OnLogsClicked);
        var removeItem = CreateItem("Remove app data & exit", OnRemoveClicked);
        var exitItem = CreateItem("Exit", OnExitClicked);

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_autoRestoreItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_profileItems[ProfileIds.Headphones]);
        menu.Items.Add(_profileItems[ProfileIds.Speakers]);
        menu.Items.Add(new Separator());
        menu.Items.Add(captureItem);
        menu.Items.Add(applyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);
        menu.Items.Add(exitItem);

        _icon.ContextMenu = menu;
        ApplyMenuTheme(menu);

        // The menu is built once; re-sync checkable state every time it
        // opens. Autostart comes from the registry (source of truth), the
        // rest from the last saved snapshot - so changes made in the
        // settings window or elsewhere are reflected even without a Saved
        // notification reaching this menu.
        menu.Opened += (_, _) =>
        {
            _autoRestoreItem.IsChecked = _lastSettings?.AutoRestoreEnabled ?? _autoRestoreItem.IsChecked;
            _autostartItem.IsChecked = _autostart.IsEnabled();
            _activeProfileId = _lastSettings?.ActiveProfileId ?? _activeProfileId;
            SyncProfileChecks();
        };

        _icon.TrayLeftMouseUp += (_, _) => OpenSettingsRequested?.Invoke();
        _icon.Visibility = Visibility.Visible;

        // H.NotifyIcon only creates the native icon inside its own Loaded
        // handler, which never fires for a TaskbarIcon created in code
        // outside a visual tree. ForceCreate performs the same registration
        // explicitly. enablesEfficiencyMode:false matches the library's own
        // Loaded handler; the default true would put the process into a
        // throttled efficiency state.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// Shows a one-time hint pointing to the Windows 11 hidden-icons chevron.
    /// New tray icons are not pinned by default, so users often cannot find
    /// a perfectly healthy tray application.
    /// </summary>
    public void ShowFirstRunHint()
    {
        try
        {
            _icon.ShowNotification(
                "SoundController is running",
                "The tray icon may be hidden under the chevron (^) next to the clock. " +
                "Right-click the taskbar > Taskbar settings > Other system tray icons to pin it.");
        }
        catch (Exception ex)
        {
            // Balloons can fail (notifications off, no focus assistant, etc.);
            // the tray itself is already working, so this is cosmetic only.
            _logger.LogDebug(ex, "First-run balloon hint could not be shown");
        }
    }

    private static MenuItem CreateItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private MenuItem CreateProfileItem(string profileId)
    {
        var item = new MenuItem
        {
            Header = ProfileIds.DisplayNameFor(profileId),
            IsCheckable = true,
            Tag = profileId,
        };
        item.Click += OnProfileClicked;
        return item;
    }

    /// <summary>
    /// Applies the keyed dark-green styles to the tray menu. Explicit keyed
    /// styles are required because implicit Application styles never reach a
    /// ContextMenu popup (and once leaked into it via internal TextBlocks,
    /// making the text unreadable). Missing resources fall back to the
    /// native menu - cosmetics must never block the tray from working.
    /// </summary>
    private static void ApplyMenuTheme(ContextMenu menu)
    {
        Style? menuStyle = Application.Current?.TryFindResource("DarkContextMenuStyle") as Style;
        Style? itemStyle = Application.Current?.TryFindResource("DarkMenuItemStyle") as Style;
        Style? separatorStyle = Application.Current?.TryFindResource("DarkMenuSeparatorStyle") as Style;
        if (menuStyle is null || itemStyle is null || separatorStyle is null)
        {
            return;
        }

        menu.Style = menuStyle;
        foreach (var item in menu.Items)
        {
            if (item is MenuItem menuItem)
            {
                menuItem.Style = itemStyle;
            }
            else if (item is Separator separator)
            {
                separator.Style = separatorStyle;
            }
        }
    }

    private async void OnAutoRestoreToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            var load = await _settingsService.LoadAsync().ConfigureAwait(true);
            var settings = load.Settings ?? new AppSettings();
            settings.AutoRestoreEnabled = _autoRestoreItem.IsChecked;
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
            _logger.LogInformation("Auto-restore toggled to {Enabled}", settings.AutoRestoreEnabled);

            if (settings.AutoRestoreEnabled)
            {
                // Re-check immediately so the user sees protection resume.
                _coordinator.RequestRestore("auto-restore enabled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling auto-restore failed");
        }
    }

    private async void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            _autostart.SetEnabled(_autostartItem.IsChecked);
            var load = await _settingsService.LoadAsync().ConfigureAwait(true);
            var settings = load.Settings ?? new AppSettings();
            settings.StartWithWindows = _autostartItem.IsChecked;
            await _settingsService.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling autostart failed");
            _statusItem.Header = "Autostart change failed - see logs";
        }
    }

    private async void OnProfileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string profileId)
        {
            return;
        }

        try
        {
            // Persisting the switch plus a forced apply lives in the
            // coordinator; the resulting ActiveProfileChanged re-syncs the
            // checkmarks (also covering switches made elsewhere).
            await _coordinator.ActivateProfileAsync(profileId).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the click; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching active profile to {ProfileId} failed", profileId);
            _statusItem.Header = "Profile switch failed - see logs";
            // The click checked the item, but the switch did not happen:
            // restore the visual to the actually active profile.
            SyncProfileChecks();
        }
    }

    private void OnActiveProfileChanged(string profileId)
    {
        // Fired from background continuations; marshal the visual update.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _activeProfileId = profileId;
            SyncProfileChecks();
        });
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        // A save anywhere (tray toggles, settings window, coordinator) keeps
        // the menu checkmarks in sync with what is actually persisted.
        _lastSettings = settings;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _autoRestoreItem.IsChecked = settings.AutoRestoreEnabled;
            _autostartItem.IsChecked = settings.StartWithWindows;
            _activeProfileId = settings.ActiveProfileId;
            SyncProfileChecks();
        });
    }

    private void SyncProfileChecks()
    {
        foreach (var (profileId, item) in _profileItems)
        {
            item.IsChecked = profileId == _activeProfileId;
        }
    }

    private async void OnCaptureClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _coordinator.CaptureCurrentStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture from tray failed");
            _statusItem.Header = "Capture failed - see logs";
        }
    }

    private async void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _coordinator.RestoreNowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual apply from tray failed");
            _statusItem.Header = "Apply failed - see logs";
        }
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();

    private void OnLogsClicked(object sender, RoutedEventArgs e)
    {
        string logDirectory = Path.Combine(AppDataPaths.DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", logDirectory) { UseShellExecute = true });
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(
            "This removes the SoundController autostart entry and deletes\n\n" +
            AppDataPaths.DataDirectory +
            "\n\n(settings and logs), then exits the application.\n\nContinue?",
            "SoundController - remove app data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Yes)
        {
            CleanupRequested?.Invoke();
        }
    }

    private void OnStatusChanged(ProtectionStatus status)
    {
        // Events arrive from background threads; marshal the visual update.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _statusItem.Header = status.Message;
            _icon.ToolTipText = $"SoundController - {status.Message}";
            SetIcon(status.State);
        });
    }

    /// <summary>
    /// Draws the tray icon: a speaker glyph with a status dot. Regenerating a
    /// small bitmap per status change is cheap and avoids shipping icon files.
    /// </summary>
    private void SetIcon(ProtectionState state)
    {
        System.Drawing.Icon icon = CreateIcon(state);

        // Icon.FromHandle wraps the GDI handle without owning it, and we do
        // not know whether H.NotifyIcon keeps internal references to previous
        // icon objects. Destroying a handle that is still referenced would
        // blank the tray icon, so handles are retained here and freed once in
        // Dispose. Cost is a few KB for a tray session - safely bounded.
        _icon.Icon = icon;
        _liveIconHandles.Add(icon.Handle);
    }

    private static System.Drawing.Icon CreateIcon(ProtectionState state)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(32, 33, 36));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            // Simple speaker: rectangle + sound cone.
            var white = System.Drawing.Brushes.White;
            graphics.FillRectangle(white, 8, 13, 5, 6);
            graphics.FillPolygon(white, new[]
            {
                new System.Drawing.Point(13, 13),
                new System.Drawing.Point(19, 7),
                new System.Drawing.Point(19, 25),
                new System.Drawing.Point(13, 19),
            });

            var dotColor = state switch
            {
                ProtectionState.Protected => System.Drawing.Color.MediumSeaGreen,
                ProtectionState.Restoring => System.Drawing.Color.Goldenrod,
                ProtectionState.SavedDeviceUnavailable => System.Drawing.Color.Orange,
                ProtectionState.UnsupportedMode => System.Drawing.Color.Orange,
                ProtectionState.Degraded => System.Drawing.Color.Orange,
                ProtectionState.SonarDisconnected => System.Drawing.Color.OrangeRed,
                ProtectionState.Disabled => System.Drawing.Color.Gray,
                _ => System.Drawing.Color.Gray,
            };
            using var dot = new System.Drawing.SolidBrush(dotColor);
            graphics.FillEllipse(dot, 22, 22, 8, 8);
        }

        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _coordinator.StatusChanged -= OnStatusChanged;
        _coordinator.ActiveProfileChanged -= OnActiveProfileChanged;
        _settingsService.Saved -= OnSettingsSaved;
        _icon.Dispose();

        // HICONs are only safe to free after the tray icon is gone.
        foreach (IntPtr handle in _liveIconHandles)
        {
            _ = DestroyIcon(handle);
        }

        _liveIconHandles.Clear();
    }
}

/// <summary>Centralizes app data paths so every service agrees on them.</summary>
public static class AppDataPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundController");
}
