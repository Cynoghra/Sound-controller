namespace SoundController.Config;

/// <summary>
/// Shape of settings.json as written by schema version 1: one flat
/// configuration without profiles. Kept only so the migration can read old
/// files losslessly after the top-level Windows/Sonar properties moved into
/// <see cref="ProfileSettings"/>.
/// </summary>
public sealed class AppSettingsV1
{
    public int SchemaVersion { get; set; } = 1;
    public bool AutoRestoreEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public WindowsDefaultsSettings? Windows { get; set; }
    public SonarDefaultsSettings? Sonar { get; set; }
    public Dictionary<string, string>? DeviceNames { get; set; }
}

/// <summary>Pure schema migrations. Each step takes the previous shape and produces the current one.</summary>
public static class AppSettingsMigrations
{
    /// <summary>
    /// Migrates a schema 1 document to schema 2. The single captured state
    /// becomes the Headphones profile and stays active, so auto-restore keeps
    /// enforcing exactly what it enforced before the upgrade; the Speakers
    /// profile starts empty and gets a clear "not captured yet" status until
    /// the user captures it deliberately.
    /// </summary>
    public static AppSettings V1ToV2(AppSettingsV1 legacy)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            AutoRestoreEnabled = legacy.AutoRestoreEnabled,
            StartWithWindows = legacy.StartWithWindows,
            ActiveProfileId = ProfileIds.Headphones,
        };

        settings.Profiles[ProfileIds.Headphones] = new ProfileSettings
        {
            Name = ProfileIds.DisplayNameFor(ProfileIds.Headphones),
            Windows = legacy.Windows,
            Sonar = legacy.Sonar,
        };
        settings.Profiles[ProfileIds.Speakers] = new ProfileSettings
        {
            Name = ProfileIds.DisplayNameFor(ProfileIds.Speakers),
        };

        if (legacy.DeviceNames is not null)
        {
            settings.DeviceNames = new Dictionary<string, string>(
                legacy.DeviceNames, StringComparer.OrdinalIgnoreCase);
        }

        return settings;
    }
}
