using SteelSeriesAPI.Sonar;
using SteelSeriesAPI.Sonar.Enums;
using SteelSeriesAPI.Sonar.Events;
using SteelSeriesAPI.Sonar.Models;
using Microsoft.Extensions.Logging;
using SoundController.Core;

namespace SoundController.Sonar;

/// <summary>Connection/mode state shown in the tray.</summary>
public enum SonarConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    UnsupportedMode,
}

/// <summary>One selectable Sonar device for settings UI dropdowns.</summary>
public sealed record SonarDeviceOption(string Id, string Name, bool IsSonarVirtual, AudioDirectionHint Flow);

public enum AudioDirectionHint
{
    Render,
    Capture,
}

/// <summary>
/// Abstraction over the Sonar client so restore coordination and UI can be
/// tested without SteelSeries GG. The unofficial Sonar API is used only
/// behind this interface (see agents.md "Library-Specific Cautions").
/// </summary>
public interface ISonarService : IDisposable
{
    /// <summary>Raised for any Sonar-side change hint; may fire from polling or socket threads.</summary>
    event Action<string>? StateHint;

    event Action<SonarConnectionState>? ConnectionChanged;

    SonarConnectionState ConnectionState { get; }

    /// <summary>Wires events and starts the listener. Safe to call while GG is down; the listener reconnects.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();

    /// <summary>
    /// Reads current Classic-mode redirections and known devices. Throws
    /// <see cref="SonarUnsupportedModeException"/> when GG is in Streamer mode.
    /// Returns null only when Sonar cannot be reached at all.
    /// </summary>
    Task<SonarSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SonarDeviceOption>> ListDevicesAsync(CancellationToken cancellationToken);

    Task ApplyAsync(IReadOnlyList<SonarCorrection> corrections, CancellationToken cancellationToken);
}

/// <summary>Raised when Sonar is running but in Streamer mode, which this build does not control.</summary>
public sealed class SonarUnsupportedModeException : Exception
{
    public Mode Mode { get; }

    public SonarUnsupportedModeException(Mode mode)
        : base($"Sonar is in {mode} mode; this build supports Classic mode only.")
    {
        Mode = mode;
    }
}

public sealed class SonarService : ISonarService
{
    // Sonar's websocket alone does not surface every redirection change; the
    // library README states full detection needs a polling interval. Do not
    // remove this value when it looks like clutter (see agents.md).
    private static readonly TimeSpan SonarPollingInterval = TimeSpan.FromMilliseconds(500);

    // Sonar's backend was observed racing a read-back one second after a
    // write, and the UI showed "something went wrong" after rapid state
    // churn. Spacing redirection writes keeps the unofficial API from
    // receiving bursts. An exception during a batch stops the remaining
    // writes (the coordinator reports degraded status and the next event
    // re-plans) instead of hammering a backend that is already failing.
    private static readonly TimeSpan SonarWriteSpacing = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<SonarService> _logger;
    private readonly SonarClient _client;
    private SonarEventListener? _events;
    private SonarConnectionState _connectionState = SonarConnectionState.Connecting;

    public event Action<string>? StateHint;
    public event Action<SonarConnectionState>? ConnectionChanged;

    public SonarConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (_connectionState == value)
            {
                return;
            }

            _connectionState = value;
            ConnectionChanged?.Invoke(value);
        }
    }

    public SonarService(ILogger<SonarService> logger, ILogger rawLogger)
    {
        _logger = logger;
        // SonarClient's constructor takes the non-generic ILogger; discovery
        // and reconnection across GG restarts are handled inside the library.
        _client = new SonarClient(rawLogger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _events = _client.Events;
        _events.PollingInterval = SonarPollingInterval;
        _events.Connected += OnConnected;
        _events.Disconnected += OnDisconnected;
        _events.ModeChanged += OnModeChanged;
        _events.ClassicDeviceChanged += OnClassicDeviceChanged;
        _events.MicDeviceChanged += OnMicDeviceChanged;
        _events.AudioDeviceStatusChanged += OnAudioDeviceStatusChanged;
        _events.RedirectionsInvalidated += OnRedirectionsInvalidated;
        _events.Start();

        ConnectionState = SonarConnectionState.Connecting;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_events is not null)
        {
            _events.Connected -= OnConnected;
            _events.Disconnected -= OnDisconnected;
            _events.ModeChanged -= OnModeChanged;
            _events.ClassicDeviceChanged -= OnClassicDeviceChanged;
            _events.MicDeviceChanged -= OnMicDeviceChanged;
            _events.AudioDeviceStatusChanged -= OnAudioDeviceStatusChanged;
            _events.RedirectionsInvalidated -= OnRedirectionsInvalidated;
            try
            {
                _events.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Listener stop failures during shutdown are not fatal; the
                // client is disposed right after.
                _logger.LogWarning(ex, "Sonar listener stop failed during shutdown");
            }
        }

        return Task.CompletedTask;
    }

    public async Task<SonarSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        // Guard the mode first: in Streamer mode the Classic endpoints either
        // fail or mean something different, and we want a clear status.
        var mode = await _client.Mode.GetAsync(cancellationToken).ConfigureAwait(false);
        if (mode != Mode.Classic)
        {
            ConnectionState = SonarConnectionState.UnsupportedMode;
            throw new SonarUnsupportedModeException(mode);
        }

        ConnectionState = SonarConnectionState.Connected;
        var redirections = await _client.Redirections.GetClassicRedirectionsAsync(cancellationToken).ConfigureAwait(false);
        var devices = await _client.Devices.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var channelDevices = new Dictionary<Channel, string?>();
        foreach (var redirection in redirections)
        {
            channelDevices[redirection.Channel] = redirection.DeviceId;
        }

        var knownIds = new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        return new SonarSnapshot(channelDevices, knownIds);
    }

    public async Task<IReadOnlyList<SonarDeviceOption>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await _client.Devices.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(d => new SonarDeviceOption(
                d.Id,
                d.Name,
                d.IsSonarVirtual,
                d.DataFlow == AudioDataFlow.Capture ? AudioDirectionHint.Capture : AudioDirectionHint.Render))
            .OrderBy(o => o.IsSonarVirtual) // physical first, virtual grouped after
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task ApplyAsync(IReadOnlyList<SonarCorrection> corrections, CancellationToken cancellationToken)
    {
        for (int i = 0; i < corrections.Count; i++)
        {
            var correction = corrections[i];

            // All Classic-mode channels share SetClassicDevice - including Mic.
            // The library's SetMicDeviceAsync targets streamRedirections/mic
            // (a Streamer-mode passthrough), which never changes the classic
            // mic redirection read by GetClassicRedirectionsAsync; writing
            // through it reported success while GG kept the old device. Mic is
            // a classic redirection (key "mic"), so it belongs here too.
            await _client.Redirections.SetClassicDeviceAsync(correction.Channel, correction.DesiredDeviceId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Restored Sonar {Channel} to {DeviceId}", correction.Channel, correction.DesiredDeviceId);

            if (i < corrections.Count - 1)
            {
                await Task.Delay(SonarWriteSpacing, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        ConnectionState = SonarConnectionState.Connected;
        // Redirections may have changed while we were disconnected.
        StateHint?.Invoke("Sonar connected");
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        ConnectionState = SonarConnectionState.Disconnected;
        _logger.LogWarning("Sonar disconnected; will rely on library reconnection");
    }

    private void OnModeChanged(object? sender, ModeChange e)
    {
        StateHint?.Invoke($"Sonar mode changed to {e.NewMode}");
    }

    private void OnClassicDeviceChanged(object? sender, ClassicDeviceChange e)
    {
        StateHint?.Invoke($"Sonar {e.Channel} redirected: {e.PreviousDeviceId} -> {e.NewDeviceId}");
    }

    private void OnMicDeviceChanged(object? sender, MicDeviceChange e)
    {
        StateHint?.Invoke($"Sonar mic redirected: {e.PreviousDeviceId} -> {e.NewDeviceId}");
    }

    private void OnAudioDeviceStatusChanged(object? sender, AudioDeviceStatusChange e)
    {
        // A device appearing or disappearing can change availability of a
        // locked endpoint or trigger Windows auto-switching; either way the
        // coordinator re-reads full state, this is only a hint.
        StateHint?.Invoke($"device {e.State}: {e.Name}");
    }

    private void OnRedirectionsInvalidated(object? sender, EventArgs e)
    {
        StateHint?.Invoke("Sonar redirections invalidated");
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Swallow: dispose must not throw; state is torn down regardless.
        }

        _client.Dispose();
    }
}
