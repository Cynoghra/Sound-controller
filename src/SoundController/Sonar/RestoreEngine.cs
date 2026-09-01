using SteelSeriesAPI.Sonar.Enums;
using SoundController.Config;
using SoundController.Core;

namespace SoundController.Sonar;

/// <summary>
/// Pure decision logic for restore operations. Given the saved ("locked")
/// settings and snapshots of the live Sonar and Windows state, it computes the
/// smallest set of corrections needed. It performs no I/O, so every rule here
/// is unit-testable without audio hardware or GG.
/// </summary>
public static class RestoreEngine
{
    // Channels that can actually be redirected in Classic mode. Master and
    // unknown future channels are never written: SetClassicDevice on them is
    // rejected by Sonar's backend (belt-and-braces against stale settings
    // files that still contain such entries).
    private static readonly Channel[] SettableChannels =
    {
        Channel.Game, Channel.Chat, Channel.Media, Channel.Aux, Channel.Mic,
    };

    /// <summary>
    /// Compares saved state against live snapshots and produces a plan.
    /// A null snapshot (service unreachable) simply skips that domain so a
    /// Sonar outage never blocks Windows restore or vice versa.
    /// </summary>
    public static RestorePlan Plan(AppSettings settings, SonarSnapshot? sonar, WindowsSnapshot? windows)
    {
        var sonarCorrections = PlanSonar(settings.Sonar, sonar, out var unavailableSonar);
        var windowsCorrections = PlanWindows(settings.Windows, windows, out var unavailableWindows);

        // The same physical device can be locked to several roles (e.g.
        // Console and Multimedia); report it once, not once per role.
        var unavailable = unavailableSonar.Concat(unavailableWindows)
            .DistinctBy(device => device.DeviceId)
            .ToList();
        if (sonarCorrections.Count == 0 && windowsCorrections.Count == 0 && unavailable.Count == 0)
        {
            return RestorePlan.Empty;
        }

        return new RestorePlan(sonarCorrections, windowsCorrections, unavailable);
    }

    private static IReadOnlyList<SonarCorrection> PlanSonar(
        SonarDefaultsSettings? saved,
        SonarSnapshot? current,
        out List<UnavailableDevice> unavailable)
    {
        unavailable = new List<UnavailableDevice>();
        var corrections = new List<SonarCorrection>();

        if (saved is null || current is null)
        {
            return corrections;
        }

        foreach (var (channelName, desiredId) in saved.Channels)
        {
            // Unknown channel names are skipped rather than fatal: a GG update
            // may add channels this build does not know about.
            if (!Enum.TryParse<Channel>(channelName, ignoreCase: false, out var channel))
            {
                continue;
            }

            if (!SettableChannels.Contains(channel))
            {
                continue;
            }

            // Nothing locked for this channel means restore leaves it alone.
            if (string.IsNullOrEmpty(desiredId))
            {
                continue;
            }

            // If Sonar does not currently report the channel at all (fresh GG
            // install, removed channel), leave it untouched rather than guessing.
            if (!current.Devices.TryGetValue(channel, out var currentId))
            {
                continue;
            }

            if (string.IsNullOrEmpty(currentId))
            {
                // Sonar cleared this redirection - the "red, no device" state
                // observed after backend churn. The channel entry exists, so
                // this is explicit emptiness, not a missing channel: heal it
                // by writing the locked device back.
                corrections.Add(new SonarCorrection(channel, string.Empty, desiredId));
                continue;
            }

            if (currentId == desiredId)
            {
                // Already correct; writing again would only risk event loops.
                continue;
            }

            if (!current.KnownDeviceIds.Contains(desiredId))
            {
                // Per project policy: never fall back to an arbitrary device.
                // Report the missing one and wait for it to reappear.
                unavailable.Add(new UnavailableDevice(desiredId, null, $"Sonar channel {channel}"));
                continue;
            }

            corrections.Add(new SonarCorrection(channel, currentId ?? string.Empty, desiredId));
        }

        return corrections;
    }

    private static IReadOnlyList<WindowsCorrection> PlanWindows(
        WindowsDefaultsSettings? saved,
        WindowsSnapshot? current,
        out List<UnavailableDevice> unavailable)
    {
        unavailable = new List<UnavailableDevice>();
        var corrections = new List<WindowsCorrection>();

        if (saved is null || current is null)
        {
            return corrections;
        }

        foreach (var (key, desiredId) in SavedWindowsRoles(saved))
        {
            if (string.IsNullOrEmpty(desiredId))
            {
                continue;
            }

            if (!current.CurrentDefaults.TryGetValue(key, out var currentId))
            {
                continue;
            }

            if (currentId == desiredId)
            {
                continue;
            }

            if (!current.AvailableIds(key.Direction).Contains(desiredId))
            {
                unavailable.Add(new UnavailableDevice(desiredId, null, $"Windows {key.Direction} {key.Role} default"));
                continue;
            }

            corrections.Add(new WindowsCorrection(key, currentId ?? string.Empty, desiredId));
        }

        return corrections;
    }

    /// <summary>
    /// Enumerates the six persisted role slots. Keeping this in one place makes
    /// the "all roles are independent" rule explicit.
    /// </summary>
    public static IEnumerable<KeyValuePair<AudioRoleKey, string?>> SavedWindowsRoles(WindowsDefaultsSettings saved)
    {
        yield return new(new AudioRoleKey(AudioDirection.Render, DefaultRole.Console), saved.PlaybackConsoleId);
        yield return new(new AudioRoleKey(AudioDirection.Render, DefaultRole.Multimedia), saved.PlaybackMultimediaId);
        yield return new(new AudioRoleKey(AudioDirection.Render, DefaultRole.Communications), saved.PlaybackCommunicationsId);
        yield return new(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Console), saved.RecordingConsoleId);
        yield return new(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Multimedia), saved.RecordingMultimediaId);
        yield return new(new AudioRoleKey(AudioDirection.Capture, DefaultRole.Communications), saved.RecordingCommunicationsId);
    }
}
