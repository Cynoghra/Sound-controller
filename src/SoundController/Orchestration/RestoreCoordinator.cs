using Microsoft.Extensions.Logging;
using SteelSeriesAPI.Sonar.Enums;
using SoundController.Config;
using SoundController.Core;
using SoundController.WindowsAudio;
using SonarSvc = SoundController.Sonar;

namespace SoundController.Orchestration;

/// <summary>
/// Channels this build is allowed to capture and redirect in Classic mode.
/// Master is a mixer concept, not a redirection target; storing it would
/// produce a write Sonar rejects.
/// </summary>
internal static class SettableChannels
{
    public static readonly Channel[] All =
    {
        Channel.Game, Channel.Chat, Channel.Media, Channel.Aux, Channel.Mic,
    };
}

/// <summary>Coarse protection state surfaced to the tray.</summary>
public enum ProtectionState
{
    Disabled,
    Protected,
    Restoring,
    SavedDeviceUnavailable,
    SonarDisconnected,
    UnsupportedMode,
    Degraded,
}

/// <summary>Current protection status for UI display.</summary>
public sealed record ProtectionStatus(ProtectionState State, string Message);

/// <summary>
/// Coordinates restore work: receives change hints from Sonar and Windows
/// audio, debounces bursts, computes a plan via <see cref="RestoreEngine"/>,
/// applies only the corrections that differ, and reports status.
///
/// Concurrency model: restore passes are serialized through one semaphore;
/// hints are coalesced in a queue; a suppression window after applying
/// prevents our own writes from re-triggering a restore loop.
/// </summary>
public sealed class RestoreCoordinator : IDisposable
{
    // GG can emit several endpoint events while Windows is still enumerating a
    // USB device. Coalescing them before reading state avoids restoring
    // against a partial device list. 750ms is imperceptible but covers the
    // observed event bursts.
    private static readonly TimeSpan DeviceChangeDebounce = TimeSpan.FromMilliseconds(750);

    // After applying corrections we ignore incoming hints briefly: our own
    // writes trigger Sonar/Windows events of their own. Without this window an
    // applied correction would be re-read, possibly still appear different
    // (backend not settled), and be written again - an event loop. Tradeoff: a
    // user change made within ~2s after a restore is picked up on the next
    // event instead, which is harmless by comparison.
    private static readonly TimeSpan ApplySuppressionWindow = TimeSpan.FromSeconds(2);

    // Reason array reused by every manual apply (CA1861: never re-allocate).
    private static readonly string[] ManualApplyReasons = ["manual apply"];

    private readonly SonarSvc.ISonarService _sonar;
    private readonly IWindowsAudioService _windows;
    private readonly SettingsService _settingsService;
    private readonly ILogger<RestoreCoordinator> _logger;

    private readonly object _sync = new();
    private readonly Queue<string> _pendingReasons = new();
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _drainScheduled;
    private DateTimeOffset _suppressUntilUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public event Action<ProtectionStatus>? StatusChanged;

    /// <summary>
    /// Raised after the active profile was switched and persisted. UIs use it
    /// to keep toggle checkmarks and indicators in sync regardless of which
    /// surface (tray, settings window) performed the switch.
    /// </summary>
    public event Action<string>? ActiveProfileChanged;

    public RestoreCoordinator(
        SonarSvc.ISonarService sonar,
        IWindowsAudioService windows,
        SettingsService settingsService,
        ILogger<RestoreCoordinator> logger)
    {
        _sonar = sonar;
        _windows = windows;
        _settingsService = settingsService;
        _logger = logger;

        _sonar.StateHint += RequestRestore;
        _windows.StateHint += RequestRestore;
    }

    /// <summary>
    /// Entry point for change hints. Never throws, never blocks the caller,
    /// and coalesces bursts into a single debounced restore pass.
    /// </summary>
    public void RequestRestore(string reason)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _suppressUntilUtc)
            {
                _logger.LogDebug("Hint suppressed ({Reason})", reason);
                return;
            }

            _pendingReasons.Enqueue(reason);
            if (_drainScheduled)
            {
                return;
            }

            _drainScheduled = true;
        }

        // Fire-and-forget with observed exceptions: the drain loop owns error
        // handling and resets the scheduled flag on every exit path.
        _ = DrainAsync();
    }

    /// <summary>Manual "apply saved setup now". Bypasses debounce, suppression, and the auto-restore toggle.</summary>
    public Task RestoreNowAsync()
    {
        return RestoreInternalAsync(ManualApplyReasons, forceApply: true);
    }

    /// <summary>
    /// Switches the active configuration and applies it immediately. Bypasses
    /// debounce, suppression, and the auto-restore toggle: an explicit toggle
    /// is user intent, and the apply opens the post-apply suppression window
    /// so the writes' own event echo is absorbed. The switch is persisted
    /// first, so even if the apply fails, auto-restore keeps enforcing the
    /// newly selected profile and later hints retry it.
    /// </summary>
    public async Task ActivateProfileAsync(string profileId)
    {
        var cancellationToken = _lifetime.Token;
        string reason;

        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var settings = load.Settings ?? new AppSettings();

            if (!settings.Profiles.TryGetValue(profileId, out var profile))
            {
                Report(new ProtectionStatus(ProtectionState.Degraded,
                    $"Unknown configuration '{profileId}' - settings may have been edited by hand"));
                return;
            }

            settings.ActiveProfileId = profileId;
            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Active profile switched to {ProfileId} ({Name})", profileId, profile.Name);
            reason = $"profile switch to {profile.Name}";

            // Fire while still inside the gate: handlers only update visuals,
            // and firing before the apply means the UI already reflects the
            // persisted state even if the apply then fails.
            ActiveProfileChanged?.Invoke(profileId);
        }
        finally
        {
            _restoreGate.Release();
        }

        // Outside the gate because RestoreInternalAsync takes it itself. A
        // concurrent hint pass slipping in between release and re-acquire is
        // harmless: settings already hold the new active profile on disk.
        await RestoreInternalAsync(new[] { reason }, forceApply: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads live state of both domains and stores it as the active profile's
    /// locked state. If one domain is unreachable, the other is still captured
    /// and the failure is surfaced through status and logs.
    /// </summary>
    public async Task CaptureCurrentStateAsync()
    {
        var cancellationToken = _lifetime.Token;
        Report(new ProtectionStatus(ProtectionState.Restoring, "Capturing current devices..."));

        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var settings = load.Settings ?? new AppSettings();

            var profile = EnsureActiveProfile(settings);

            var sonar = await TryReadSonarSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var windows = await TryReadWindowsSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (sonar.Snapshot is null && windows is null)
            {
                Report(new ProtectionStatus(ProtectionState.Degraded,
                    "Nothing captured: neither Sonar nor Windows state was readable"));
                return;
            }

            if (sonar.Snapshot is not null)
            {
                profile.Sonar = new SonarDefaultsSettings
                {
                    Channels = sonar.Snapshot.Devices
                        .Where(kvp => SettableChannels.All.Contains(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                        .ToDictionary(kvp => kvp.Key.ToString(), kvp => (string?)kvp.Value, StringComparer.Ordinal),
                };
            }

            if (windows is not null)
            {
                profile.Windows = new WindowsDefaultsSettings
                {
                    PlaybackConsoleId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Render, DefaultRole.Console)),
                    PlaybackMultimediaId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Render, DefaultRole.Multimedia)),
                    PlaybackCommunicationsId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Render, DefaultRole.Communications)),
                    RecordingConsoleId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Console)),
                    RecordingMultimediaId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Multimedia)),
                    RecordingCommunicationsId = windows.CurrentDefaults.GetValueOrDefault(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Communications)),
                };
            }

            await CacheDeviceNamesAsync(settings, cancellationToken).ConfigureAwait(false);
            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            Report(new ProtectionStatus(ProtectionState.Protected,
                $"Captured current devices for {ActiveProfileDisplayName(settings)}"));
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                // The delay happens before taking the snapshot of reasons, so
                // a burst arriving during the delay coalesces into one pass.
                await Task.Delay(DeviceChangeDebounce, _lifetime.Token).ConfigureAwait(false);

                string[] reasons;
                lock (_sync)
                {
                    if (_pendingReasons.Count == 0)
                    {
                        _drainScheduled = false;
                        return;
                    }

                    reasons = _pendingReasons.ToArray();
                    _pendingReasons.Clear();
                }

                // A queued hint can execute inside the post-apply suppression
                // window: it may have been enqueued before the apply set the
                // window, and the drain only runs it ~750ms later. Running it now
                // re-reads state before the backend settled and re-writes the
                // correction we just applied - the echo double-write observed in
                // logs that destabilized Sonar's channels. Defer until the
                // window ends instead.
                TimeSpan suppressionRemaining = _suppressUntilUtc - DateTimeOffset.UtcNow;
                if (suppressionRemaining > TimeSpan.Zero)
                {
                    _logger.LogDebug(
                        "Queued restore deferred {Ms}ms behind the post-apply suppression window",
                        (int)suppressionRemaining.TotalMilliseconds);
                    await Task.Delay(suppressionRemaining, _lifetime.Token).ConfigureAwait(false);
                }

                await RestoreInternalAsync(reasons, forceApply: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            { _drainScheduled = false; }
        }
        catch (Exception ex)
        {
            // A crash here must never take the tray app down (agents.md):
            // log, reschedule cleanly, and let the next hint retry.
            _logger.LogError(ex, "Restore drain loop failed");
            lock (_sync)
            { _drainScheduled = false; }
        }
    }

    private async Task RestoreInternalAsync(string[] reasons, bool forceApply)
    {
        var cancellationToken = _lifetime.Token;
        _logger.LogInformation("Restore pass started: {Reasons}", string.Join("; ", reasons));

        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(new ProtectionStatus(ProtectionState.Restoring, "Checking devices..."));

            var load = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var settings = load.Settings ?? new AppSettings();
            if (!forceApply && !settings.AutoRestoreEnabled)
            {
                Report(new ProtectionStatus(ProtectionState.Disabled, "Auto-restore is off"));
                return;
            }

            if (!settings.HasLockedState)
            {
                // An empty active profile plans no writes at all; reporting
                // "devices match locked state" would hide the real situation.
                // Tell the user what to do instead.
                Report(new ProtectionStatus(ProtectionState.Disabled,
                    $"No saved setup for {ActiveProfileDisplayName(settings)} yet - capture it from the tray or Settings"));
                return;
            }

            var (sonar, sonarProblem) = await TryReadSonarSnapshotAsync(cancellationToken).ConfigureAwait(false);
            WindowsSnapshot? windows = null;
            try
            {
                windows = await _windows.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Windows audio snapshot failed during restore");
            }

            if (sonar is null && windows is null)
            {
                Report(sonarProblem ?? new ProtectionStatus(ProtectionState.Degraded,
                    "Neither Sonar nor Windows state could be read"));
                return;
            }

            var plan = SonarSvc.RestoreEngine.Plan(settings, sonar, windows);
            LogPlan(plan, settings);

            int applied = 0;
            if (plan.SonarCorrections.Count > 0 && sonar is not null)
            {
                try
                {
                    await _sonar.ApplyAsync(plan.SonarCorrections, cancellationToken).ConfigureAwait(false);
                    applied += plan.SonarCorrections.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failing domain must not block the other.
                    _logger.LogError(ex, "Applying Sonar corrections failed");
                }
            }

            if (plan.WindowsCorrections.Count > 0 && windows is not null)
            {
                try
                {
                    await _windows.ApplyAsync(plan.WindowsCorrections, cancellationToken).ConfigureAwait(false);
                    applied += plan.WindowsCorrections.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Applying Windows default corrections failed");
                }
            }

            if (applied > 0)
            {
                // Our writes will produce events of their own; open the
                // suppression window before they arrive.
                _suppressUntilUtc = DateTimeOffset.UtcNow + ApplySuppressionWindow;
            }

            Report(BuildOutcomeStatus(plan, applied, settings, sonarProblem));
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    /// <summary>
    /// Returns the active profile, recreating the well-known entry when it is
    /// missing (first run before any save, or a hand-edited file). Capture
    /// always has a target this way. An unknown active profile ID - only
    /// possible through hand editing - normalizes to headphones so decisions
    /// always run on a profile the toggle can reach.
    /// </summary>
    private static ProfileSettings EnsureActiveProfile(AppSettings settings)
    {
        if (!ProfileIds.IsKnown(settings.ActiveProfileId))
        {
            settings.ActiveProfileId = ProfileIds.Headphones;
        }

        return settings.GetOrCreateProfile(settings.ActiveProfileId);
    }

    private static string ActiveProfileDisplayName(AppSettings settings) =>
        settings.ActiveProfile?.Name is { Length: > 0 } name ? name : ProfileIds.DisplayNameFor(settings.ActiveProfileId);

    private async Task<(SonarSnapshot? Snapshot, ProtectionStatus? Problem)> TryReadSonarSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _sonar.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (SonarSvc.SonarUnsupportedModeException ex)
        {
            _logger.LogWarning("Sonar is in unsupported mode: {Message}", ex.Message);
            return (null, new ProtectionStatus(ProtectionState.UnsupportedMode, ex.Message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sonar snapshot failed");
            return (null, new ProtectionStatus(ProtectionState.SonarDisconnected,
                "Sonar unavailable - Windows restore still active"));
        }
    }

    private async Task<WindowsSnapshot?> TryReadWindowsSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _windows.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture: Windows audio state unavailable");
            return null;
        }
    }

    private async Task CacheDeviceNamesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // Display names are best-effort cache entries for readable UI and
        // logs; failures here must not fail the capture.
        try
        {
            foreach (var device in await _sonar.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
            {
                settings.DeviceNames[device.Id] = device.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cache Sonar device names during capture");
        }

        try
        {
            foreach (AudioDirection direction in Enum.GetValues<AudioDirection>())
            {
                foreach (var endpoint in await _windows.ListEndpointsAsync(direction, cancellationToken).ConfigureAwait(false))
                {
                    settings.DeviceNames[endpoint.Id] = endpoint.Name;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cache Windows endpoint names during capture");
        }
    }

    private void LogPlan(RestorePlan plan, AppSettings settings)
    {
        if (plan.RequiresNoWrites && plan.Unavailable.Count == 0)
        {
            _logger.LogDebug("Restore plan: state already matches");
            return;
        }

        foreach (var correction in plan.SonarCorrections)
        {
            _logger.LogInformation(
                "Plan: Sonar {Channel}: '{Current}' -> '{Desired}'",
                correction.Channel,
                settings.DisplayNameFor(correction.CurrentDeviceId),
                settings.DisplayNameFor(correction.DesiredDeviceId));
        }

        foreach (var correction in plan.WindowsCorrections)
        {
            _logger.LogInformation(
                "Plan: Windows {Direction}/{Role}: '{Current}' -> '{Desired}'",
                correction.Role.Direction,
                correction.Role.Role,
                settings.DisplayNameFor(correction.CurrentDeviceId),
                settings.DisplayNameFor(correction.DesiredDeviceId));
        }

        foreach (var unavailable in plan.Unavailable)
        {
            _logger.LogWarning(
                "Locked device unavailable, waiting for it to return: '{Device}' ({Context})",
                settings.DisplayNameFor(unavailable.DeviceId),
                unavailable.Context);
        }
    }

    private static ProtectionStatus BuildOutcomeStatus(
        RestorePlan plan,
        int applied,
        AppSettings settings,
        ProtectionStatus? sonarProblem)
    {
        if (plan.Unavailable.Count > 0)
        {
            var first = plan.Unavailable[0];
            string extra = plan.Unavailable.Count > 1 ? $" (+{plan.Unavailable.Count - 1} more)" : string.Empty;
            return new ProtectionStatus(
                ProtectionState.SavedDeviceUnavailable,
                $"Saved device unavailable: {settings.DisplayNameFor(first.DeviceId)}{extra}. Waiting for it to return.");
        }

        string profile = $" ({ActiveProfileDisplayName(settings)})";
        if (applied > 0)
        {
            return new ProtectionStatus(ProtectionState.Protected, $"Restored {applied} setting(s) to locked state{profile}");
        }

        return sonarProblem
            ?? new ProtectionStatus(ProtectionState.Protected, $"Devices match locked state{profile}");
    }

    private void Report(ProtectionStatus status)
    {
        _logger.LogInformation("Status: {State}: {Message}", status.State, status.Message);
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _sonar.StateHint -= RequestRestore;
        _windows.StateHint -= RequestRestore;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _restoreGate.Dispose();
    }
}
