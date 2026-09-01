namespace SoundController.Config;

/// <summary>
/// Persisted application settings. Stored as JSON beneath
/// %LOCALAPPDATA%\SoundController\settings.json by <c>SettingsService</c>.
/// Devices are referenced by endpoint ID; <see cref="DeviceNames"/> only
/// caches display names for UI and logs.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Schema version written by this build. Loading code migrates older
    /// versions and refuses newer ones loudly instead of guessing.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>When false, change events are ignored; manual "apply now" still works.</summary>
    public bool AutoRestoreEnabled { get; set; } = true;

    /// <summary>Mirrors the HKCU Run key; the registry remains the source of truth.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Locked Windows default endpoints. Null until the user captured a state.</summary>
    public WindowsDefaultsSettings? Windows { get; set; }

    /// <summary>Locked Sonar Classic redirections. Null until captured.</summary>
    public SonarDefaultsSettings? Sonar { get; set; }

    /// <summary>Endpoint ID to display-name cache, used only for readable UI and logs.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when at least one domain has a captured ("locked") state.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLockedState => Windows is not null || Sonar is not null;

    /// <summary>Looks up a cached display name; returns the ID itself when unknown.</summary>
    public string DisplayNameFor(string deviceId) =>
        DeviceNames.TryGetValue(deviceId, out var name) ? name : deviceId;
}

/// <summary>
/// Locked Windows defaults, one endpoint ID per role. A null entry means the
/// user has not locked that role, so restore leaves it alone. Windows treats
/// Console and Multimedia as a pair in its UI, so the settings window edits
/// them together, but both are persisted independently.
/// </summary>
public sealed class WindowsDefaultsSettings
{
    public string? PlaybackConsoleId { get; set; }
    public string? PlaybackMultimediaId { get; set; }
    public string? PlaybackCommunicationsId { get; set; }
    public string? RecordingConsoleId { get; set; }
    public string? RecordingMultimediaId { get; set; }
    public string? RecordingCommunicationsId { get; set; }
}

/// <summary>
/// Locked Sonar Classic redirections keyed by <see cref="SteelSeriesAPI.Sonar.Enums.Channel"/>
/// enum name ("Game", "Chat", ...). Values are endpoint IDs; null/missing keys
/// are left untouched by restore.
/// </summary>
public sealed class SonarDefaultsSettings
{
    public Dictionary<string, string?> Channels { get; set; } = new(StringComparer.Ordinal);
}
