using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SoundController.Core;
using SoundController.Sonar;


namespace SoundController.WindowsAudio;

/// <summary>One selectable endpoint for settings UI dropdowns.</summary>
public sealed record AudioEndpointOption(string Id, string Name);

/// <summary>
/// Owns Windows audio endpoint enumeration, notifications, and default-role
/// restoration. Implementation detail: NAudio handles enumeration and
/// notifications, while actually setting a default endpoint requires the
/// isolated <c>PolicyConfigInterop</c> COM layer.
/// </summary>
public interface IWindowsAudioService : IDisposable
{
    /// <summary>
    /// Raised for any Windows audio change that might require a restore pass
    /// (default changed, device added/removed, device state changed).
    /// May be raised from a COM callback thread.
    /// </summary>
    event Action<string>? StateHint;

    /// <summary>
    /// Reads current defaults for all six roles and the set of available
    /// endpoints. Returns null when Windows audio state cannot be read at all.
    /// </summary>
    Task<WindowsSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Lists active endpoints of one direction for settings UI.</summary>
    Task<IReadOnlyList<AudioEndpointOption>> ListEndpointsAsync(AudioDirection direction, CancellationToken cancellationToken);

    /// <summary>Applies default-device corrections for individual roles.</summary>
    Task ApplyAsync(IReadOnlyList<WindowsCorrection> corrections, CancellationToken cancellationToken);
}

public sealed class WindowsDefaultService : IWindowsAudioService
{
    // Windows can reject an immediate follow-up write while the previous
    // endpoint change is still settling. One short retry covers that race;
    // more aggressive retrying would fight the user's own changes.
    private static readonly TimeSpan SetDefaultRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILogger<WindowsDefaultService> _logger;
    private readonly NotificationClient _notificationClient;

    public event Action<string>? StateHint;

    public WindowsDefaultService(ILogger<WindowsDefaultService> logger)
    {
        _logger = logger;
        _notificationClient = new NotificationClient(reason => StateHint?.Invoke(reason));
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    public Task<WindowsSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        // MMDevice calls are COM; keep them off the UI thread.
        return Task.Run(() =>
        {
            try
            {
                var defaults = new Dictionary<AudioRoleKey, string?>();
                foreach (AudioDirection direction in Enum.GetValues<AudioDirection>())
                {
                    foreach (DefaultRole role in Enum.GetValues<DefaultRole>())
                    {
                        defaults[new AudioRoleKey(direction, role)] = TryGetDefaultEndpointId(direction, role);
                    }
                }

                var render = ListActiveEndpointIds(AudioDirection.Render);
                var capture = ListActiveEndpointIds(AudioDirection.Capture);
                return new WindowsSnapshot(defaults, render, capture) as WindowsSnapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reading Windows audio snapshot failed");
                return null;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioEndpointOption>> ListEndpointsAsync(AudioDirection direction, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<AudioEndpointOption>>(() =>
        {
            var flow = ToDataFlow(direction);
            var list = new List<AudioEndpointOption>();
            // Only Active devices: disabled and not-present endpoints would
            // confuse the settings dropdown with unselectable entries.
            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioEndpointOption(device.ID, device.FriendlyName));
                }
            }

            return list.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    public async Task ApplyAsync(IReadOnlyList<WindowsCorrection> corrections, CancellationToken cancellationToken)
    {
        foreach (var correction in corrections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SetDefaultWithRetryAsync(correction, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SetDefaultWithRetryAsync(WindowsCorrection correction, CancellationToken cancellationToken)
    {
        int nativeRole = ToNativeRole(correction.Role.Role);
        try
        {
            PolicyConfigInterop.SetDefaultEndpoint(correction.DesiredDeviceId, nativeRole);
            _logger.LogInformation(
                "Restored Windows {Direction} {Role} default to {DeviceId}",
                correction.Role.Direction, correction.Role.Role, correction.DesiredDeviceId);
        }
        catch (COMException ex)
        {
            // First failure is often a transient settle race (see retry delay
            // comment); one retry, then surface the failure for logs and UI.
            _logger.LogWarning(ex,
                "SetDefaultEndpoint failed for {Direction} {Role}; retrying once after {Delay}ms",
                correction.Role.Direction, correction.Role.Role, SetDefaultRetryDelay.TotalMilliseconds);

            await Task.Delay(SetDefaultRetryDelay, cancellationToken).ConfigureAwait(false);
            PolicyConfigInterop.SetDefaultEndpoint(correction.DesiredDeviceId, nativeRole);
            _logger.LogInformation(
                "Retry restored Windows {Direction} {Role} default to {DeviceId}",
                correction.Role.Direction, correction.Role.Role, correction.DesiredDeviceId);
        }
    }

    private string? TryGetDefaultEndpointId(AudioDirection direction, DefaultRole role)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(ToDataFlow(direction), ToRole(role));
            return device.ID;
        }
        catch (COMException ex)
        {
            // A role with no default (rare, e.g. no capture devices) is a
            // legitimate empty state, not an error worth a log entry.
            _logger.LogDebug(ex, "No default endpoint for {Direction} {Role}", direction, role);
            return null;
        }
    }

    private IReadOnlySet<string> ListActiveEndpointIds(AudioDirection direction)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _enumerator.EnumerateAudioEndPoints(ToDataFlow(direction), DeviceState.Active))
        {
            using (device)
            {
                ids.Add(device.ID);
            }
        }

        return ids;
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        _enumerator.Dispose();
    }

    internal static DataFlow ToDataFlow(AudioDirection direction) => direction switch
    {
        AudioDirection.Render => DataFlow.Render,
        AudioDirection.Capture => DataFlow.Capture,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

    internal static Role ToRole(DefaultRole role) => role switch
    {
        DefaultRole.Console => Role.Console,
        DefaultRole.Multimedia => Role.Multimedia,
        DefaultRole.Communications => Role.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    internal static int ToNativeRole(DefaultRole role) => role switch
    {
        DefaultRole.Console => PolicyConfigInterop.RoleConsole,
        DefaultRole.Multimedia => PolicyConfigInterop.RoleMultimedia,
        DefaultRole.Communications => PolicyConfigInterop.RoleCommunications,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>
    /// Bridges NAudio's COM notification callbacks to plain .NET hints.
    /// Property changes (volume, name) intentionally do not raise hints:
    /// they fire frequently and are not routing changes.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action<string> _onHint;

        public NotificationClient(Action<string> onHint) => _onHint = onHint;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            _onHint($"device state changed: {deviceId} -> {newState}");

        public void OnDeviceAdded(string deviceId) => _onHint($"device added: {deviceId}");

        public void OnDeviceRemoved(string deviceId) => _onHint($"device removed: {deviceId}");

        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId) =>
            _onHint($"default changed: {dataFlow}/{role} -> {defaultDeviceId}");

        public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey)
        {
            // Not a routing change; see class comment.
        }
    }
}
