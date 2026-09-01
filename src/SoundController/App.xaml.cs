using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoundController.Autostart;
using SoundController.Config;
using SoundController.Logging;
using SoundController.Orchestration;
using SoundController.Sonar;
using SoundController.Tray;
using SoundController.UI;
using SoundController.WindowsAudio;

namespace SoundController;

/// <summary>
/// Application entry point. Builds the service provider once, starts the
/// Sonar and Windows audio services, shows the tray icon, and owns
/// deterministic shutdown order (tray, coordinator, services, provider).
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;
    private bool _servicesDisposed;
    private TrayController? _tray;
    private SettingsWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // async void is the accepted pattern for WPF OnStartup; unexpected
        // failures surface through OnDispatcherUnhandledException.
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Per-session mutex: two instances in the same session would fight
        // over device writes; a second launch just exits with a message.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\SoundController.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SoundController is already running.", "SoundController", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var services = BuildServices();
        _services = services;
        var logger = services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("SoundController starting");

        var settingsService = services.GetRequiredService<SettingsService>();
        var sonar = services.GetRequiredService<ISonarService>();
        var coordinator = services.GetRequiredService<RestoreCoordinator>();
        var autostart = services.GetRequiredService<IAutostartService>();

        var load = await settingsService.LoadAsync().ConfigureAwait(true);
        if (load.Failure is { Problem: SettingsLoadProblem.UnsupportedSchemaVersion } failure)
        {
            // Never silently downgrade settings written by a newer build; the
            // user can re-capture deliberately (see agents.md Settings rules).
            MessageBox.Show(
                failure.Detail + "\n\nLocked devices will not be restored until you capture a new state in Settings.",
                "SoundController", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Start Sonar before the tray so the first status comes from reality.
        try
        {
            await sonar.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // GG may not be running yet; the listener keeps retrying and the
            // tray shows the disconnected state.
            logger.LogError(ex, "Sonar service start failed");
        }

        try
        {
            _tray = new TrayController(
                coordinator, settingsService, autostart, sonar,
                services.GetRequiredService<ILogger<TrayController>>());
            _tray.OpenSettingsRequested += ShowSettings;
            _tray.ExitRequested += () => Shutdown();
            _tray.CleanupRequested += RemoveAppDataAndExit;
            _tray.Initialize(load.Settings ?? new AppSettings());
        }
        catch (Exception ex)
        {
            // Without a tray icon the app is uncontrollable and would leave a
            // headless process holding the single-instance mutex ("already
            // running" on the next launch). Exit cleanly instead.
            logger.LogError(ex, "Tray initialization failed; exiting");
            MessageBox.Show(
                "SoundController could not show its tray icon:\n\n" + ex.Message +
                "\n\nSee the log under %LOCALAPPDATA%\\SoundController\\logs for details.\nThe application will now exit.",
                "SoundController", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (load.Settings is not { HasLockedState: true })
        {
            _tray.ShowFirstRunHint();
            OfferFirstRunCapture(coordinator, logger);
        }

        logger.LogInformation("SoundController started");
    }

    private async void OfferFirstRunCapture(RestoreCoordinator coordinator, ILogger<App> logger)
    {
        var choice = MessageBox.Show(
            "No locked device state found.\n\nCapture the CURRENT audio devices (Sonar redirections and Windows defaults) as the locked state now?",
            "SoundController - first run", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // Sonar discovery can still be in flight right after startup;
            // capture degrades gracefully and the user can re-capture later.
            await coordinator.CaptureCurrentStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "First-run capture failed");
        }
    }

    private static ServiceProvider BuildServices()
    {
        string dataDirectory = AppDataPaths.DataDirectory;

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            // Framework internals are noise unless something breaks; Sonar
            // library traffic is interesting because the API is unofficial.
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddFilter("System", LogLevel.Warning);
            builder.AddFilter("SteelSeriesAPI", LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(System.IO.Path.Combine(dataDirectory, "logs")));
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<IWindowsAudioService, WindowsDefaultService>();
        services.AddSingleton<RestoreCoordinator>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<AppCleanupService>(sp => new AppCleanupService(
            AppDataPaths.DataDirectory,
            sp.GetRequiredService<IAutostartService>(),
            sp.GetRequiredService<ILogger<AppCleanupService>>()));

        // SonarClient logs with a plain ILogger; give it its own category so
        // library traffic is easy to find in the file log.
        services.AddSingleton<ISonarService>(sp => new SonarService(
            sp.GetRequiredService<ILogger<SonarService>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("SteelSeriesAPI")));

        return services.BuildServiceProvider();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (_services is null)
        {
            return;
        }

        _settingsWindow = new SettingsWindow(
            _services.GetRequiredService<SettingsService>(),
            _services.GetRequiredService<IWindowsAudioService>(),
            _services.GetRequiredService<ISonarService>(),
            _services.GetRequiredService<RestoreCoordinator>(),
            _services.GetRequiredService<IAutostartService>(),
            _services.GetRequiredService<ILogger<SettingsWindow>>());
        _settingsWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Unhandled UI exception");

        if (_tray is null)
        {
            // The tray never came up: staying alive would leave a headless
            // process holding the single-instance mutex. Exit instead.
            MessageBox.Show(
                "SoundController failed before its tray icon was ready:\n\n" + e.Exception.Message +
                "\n\nThe application will now exit.",
                "SoundController", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            "An unexpected error occurred. Details were written to the log.\n\n" + e.Exception.Message,
            "SoundController", MessageBoxButton.OK, MessageBoxImage.Error);
        // Keep the tray app alive: a UI crash should not abandon device
        // protection.
        e.Handled = true;
    }

    /// <summary>
    /// Full removal requested from the tray: tear down everything that holds
    /// file handles (tray, services, logs), then delete autostart entry and
    /// app data, then exit. Deleting the folder before handles are released
    /// would fail on Windows.
    /// </summary>
    private void RemoveAppDataAndExit()
    {
        var logger = _services?.GetRequiredService<ILogger<App>>();

        // Resolve before disposal; the provider is unusable afterwards.
        AppCleanupService? cleanup = null;
        try
        {
            cleanup = _services?.GetRequiredService<AppCleanupService>();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
            DisposeServices();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Pre-cleanup disposal failed");
        }

        CleanupResult result = cleanup is not null
            ? cleanup.RemoveAllAppData()
            : new CleanupResult(false, false, new[] { "Cleanup service was unavailable; see logs (if they still exist)." });

        string message = result.FullyClean
            ? "Autostart entry removed and app data deleted.\nSoundController will now exit."
            : "Cleanup finished with problems:\n\n" +
              string.Join("\n\n", result.Errors) +
              "\n\nSoundController will now exit.";
        MessageBox.Show(message, "SoundController - removal", MessageBoxButton.OK,
            result.FullyClean ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Shutdown();
    }

    /// <summary>Idempotent teardown of background services and the provider.</summary>
    private void DisposeServices()
    {
        if (_services is null || _servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;
        var logger = _services.GetService<ILogger<App>>();

        try
        {
            _services.GetRequiredService<RestoreCoordinator>().Dispose();
            _services.GetRequiredService<ISonarService>().Dispose();
            _services.GetRequiredService<IWindowsAudioService>().Dispose();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Service disposal failed");
        }

        _services.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Deterministic shutdown order (agents.md): stop user-facing surfaces
        // first, then background work, then resources.
        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            _services?.GetService<ILogger<App>>()?.LogWarning(ex, "Tray disposal failed");
        }

        DisposeServices();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
