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
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>When false, change events are ignored; manual "apply now" still works.</summary>
    public bool AutoRestoreEnabled { get; set; } = true;

    /// <summary>Mirrors the HKCU Run key; the registry remains the source of truth.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// The two saved configurations ("headphones" and "speakers", see
    /// <see cref="ProfileIds"/>), keyed by stable profile ID. Names are
    /// display-only; all decisions go through the ID.
    /// </summary>
    public Dictionary<string, ProfileSettings> Profiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Profile ID of the configuration auto-restore currently enforces.</summary>
    public string ActiveProfileId { get; set; } = ProfileIds.Headphones;

    /// <summary>Endpoint ID to display-name cache, used only for readable UI and logs.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The currently enforced profile, or null when the ID is unknown
    /// (hand-edited file). Callers treat null as "nothing locked".
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProfileSettings? ActiveProfile =>
        Profiles.TryGetValue(ActiveProfileId, out var profile) ? profile : null;

    /// <summary>True when the active profile has at least one captured ("locked") domain.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLockedState => ActiveProfile is { Windows: not null } or { Sonar: not null };

    /// <summary>Looks up a cached display name; returns the ID itself when unknown.</summary>
    public string DisplayNameFor(string deviceId) =>
        DeviceNames.TryGetValue(deviceId, out var name) ? name : deviceId;

    /// <summary>
    /// Returns the profile for <paramref name="profileId"/>, recreating a
    /// missing well-known entry (first run before any save, or a hand-edited
    /// file) so capture and save always have a target. Callers pass only
    /// known IDs; unknown IDs render as themselves via
    /// <see cref="ProfileIds.DisplayNameFor"/> but are not created here.
    /// </summary>
    public ProfileSettings GetOrCreateProfile(string profileId)
    {
        if (!Profiles.TryGetValue(profileId, out var profile))
        {
            profile = new ProfileSettings { Name = ProfileIds.DisplayNameFor(profileId) };
            Profiles[profileId] = profile;
        }

        return profile;
    }
}

/// <summary>
/// One named configuration: the device state that applies while this profile
/// is active. A null domain means the user has not captured it yet, so
/// restore leaves that domain alone.
/// </summary>
public sealed class ProfileSettings
{
    /// <summary>Display-only label ("Headphones", "Speakers"); never used for decisions.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Locked Windows default endpoints. Null until captured.</summary>
    public WindowsDefaultsSettings? Windows { get; set; }

    /// <summary>Locked Sonar Classic redirections. Null until captured.</summary>
    public SonarDefaultsSettings? Sonar { get; set; }
}

/// <summary>Stable profile IDs persisted in settings; display names live on <see cref="ProfileSettings"/>.</summary>
public static class ProfileIds
{
    public const string Headphones = "headphones";
    public const string Speakers = "speakers";

    /// <summary>True when the ID is one of the well-known profiles.</summary>
    public static bool IsKnown(string profileId) => profileId is Headphones or Speakers;

    /// <summary>Display label for a profile ID; unknown IDs render as themselves.</summary>
    public static string DisplayNameFor(string profileId) => profileId switch
    {
        Headphones => "Headphones",
        Speakers => "Speakers",
        _ => profileId,
    };
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
