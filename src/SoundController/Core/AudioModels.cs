using SteelSeriesAPI.Sonar.Enums;

namespace SoundController.Core;

/// <summary>
/// Direction of an audio endpoint. Mirrors NAudio's DataFlow so the restore
/// logic never depends on NAudio types directly.
/// </summary>
public enum AudioDirection
{
    Render,
    Capture,
}

/// <summary>
/// Windows default-device roles. Windows keeps Console, Multimedia, and
/// Communications defaults independently; the Settings UI only edits the
/// Console/Multimedia pair together, so all three are persisted explicitly.
/// </summary>
public enum DefaultRole
{
    Console,
    Multimedia,
    Communications,
}

/// <summary>
/// Composite key identifying one Windows default-device slot, e.g. the
/// Communications render default.
/// </summary>
public readonly record struct AudioRoleKey(AudioDirection Direction, DefaultRole Role);

/// <summary>
/// Everything the restore engine needs to know about the live Sonar state at
/// one moment in time. Null values mean "unknown" (a channel with no locked or
/// current redirection), not "unset by the user".
/// </summary>
public sealed record SonarSnapshot(
    IReadOnlyDictionary<Channel, string?> Devices,
    IReadOnlySet<string> KnownDeviceIds);

/// <summary>
/// Snapshot of Windows default endpoints and which endpoints are currently
/// available. Built by <c>WindowsDefaultService</c>; used by the restore
/// engine to decide whether a correction is needed and possible.
/// </summary>
public sealed record WindowsSnapshot(
    IReadOnlyDictionary<AudioRoleKey, string?> CurrentDefaults,
    IReadOnlySet<string> AvailableRenderIds,
    IReadOnlySet<string> AvailableCaptureIds)
{
    /// <summary>Returns the set of available endpoint IDs for a direction.</summary>
    public IReadOnlySet<string> AvailableIds(AudioDirection direction) => direction switch
    {
        AudioDirection.Render => AvailableRenderIds,
        AudioDirection.Capture => AvailableCaptureIds,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };
}

/// <summary>
/// A requested Sonar redirection change: channel should move from
/// <see cref="CurrentDeviceId"/> to <see cref="DesiredDeviceId"/>.
/// </summary>
public sealed record SonarCorrection(Channel Channel, string CurrentDeviceId, string DesiredDeviceId);

/// <summary>
/// A requested Windows default-device change for one role slot.
/// </summary>
public sealed record WindowsCorrection(AudioRoleKey Role, string CurrentDeviceId, string DesiredDeviceId);

/// <summary>
/// A locked device that could not be applied because it is not currently
/// present. Per project policy we never pick a fallback; we report and wait.
/// </summary>
public sealed record UnavailableDevice(string DeviceId, string? DisplayName, string Context);

/// <summary>
/// The complete set of corrections the engine wants performed. Empty lists
/// mean "already correct"; <see cref="Unavailable"/> lists devices we wanted
/// but could not use yet.
/// </summary>
public sealed record RestorePlan(
    IReadOnlyList<SonarCorrection> SonarCorrections,
    IReadOnlyList<WindowsCorrection> WindowsCorrections,
    IReadOnlyList<UnavailableDevice> Unavailable)
{
    public static readonly RestorePlan Empty = new(
        Array.Empty<SonarCorrection>(),
        Array.Empty<WindowsCorrection>(),
        Array.Empty<UnavailableDevice>());

    /// <summary>True when no writes are needed at all.</summary>
    public bool RequiresNoWrites => SonarCorrections.Count == 0 && WindowsCorrections.Count == 0;

    /// <summary>Total number of state-changing writes in the plan.</summary>
    public int CorrectionCount => SonarCorrections.Count + WindowsCorrections.Count;
}
