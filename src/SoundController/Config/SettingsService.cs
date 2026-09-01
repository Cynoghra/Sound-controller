using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SoundController.Config;

/// <summary>
/// Outcome of a settings load. <see cref="Settings"/> is null when nothing
/// usable was loaded (first run, unreadable, or unsupported schema); the
/// caller decides how to recover.
/// </summary>
public sealed record SettingsLoadResult(AppSettings? Settings, SettingsLoadFailure? Failure);

/// <summary>Describes why settings could not be loaded.</summary>
public sealed record SettingsLoadFailure(SettingsLoadProblem Problem, string Detail);

public enum SettingsLoadProblem
{
    NotPresent,
    ReadFailed,
    InvalidJson,
    UnsupportedSchemaVersion,
}

/// <summary>
/// Loads and saves application settings as versioned JSON.
/// Writes are atomic (temp file + replace) so power loss cannot leave a
/// truncated settings file. A file that fails to load is preserved beside
/// the original with a timestamp suffix for diagnosis.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly string _settingsDirectory;
    private readonly ILogger<SettingsService> _logger;

    /// <summary>
    /// Raised after each successful save with the saved snapshot. UI surfaces
    /// use it to keep toggle-style controls in sync no matter which surface
    /// (tray, settings window, coordinator) performed the save. Handlers must
    /// be quick and marshal their own visual updates; the event fires on the
    /// caller's thread.
    /// </summary>
    public event Action<AppSettings>? Saved;

    public SettingsService(ILogger<SettingsService> logger)
        // Settings live under the user's profile, never beside the executable,
        // so a Program Files install still works without write access.
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundController"),
            logger)
    {
    }

    /// <summary>Testable constructor with an explicit settings directory.</summary>
    public SettingsService(string settingsDirectory, ILogger<SettingsService> logger)
    {
        _settingsDirectory = settingsDirectory;
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        _logger = logger;
    }

    /// <summary>Full path of the settings file, for the "open logs" style UI hints.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Loads settings, migrating or failing loudly per schema rules. A corrupt
    /// file is renamed (not deleted) so it can be inspected later.
    /// </summary>
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsLoadResult(null, new SettingsLoadFailure(SettingsLoadProblem.NotPresent, _settingsPath));
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Settings file could not be read: {Path}", _settingsPath);
            return new SettingsLoadResult(null, new SettingsLoadFailure(SettingsLoadProblem.ReadFailed, ex.Message));
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(null, PreserveInvalidFile(ex.Message));
        }

        if (settings is null)
        {
            return new SettingsLoadResult(null, PreserveInvalidFile("Deserialized to null"));
        }

        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            // A newer build wrote these settings. Refusing loudly beats silently
            // downgrading and losing the user's locked devices.
            var failure = new SettingsLoadFailure(
                SettingsLoadProblem.UnsupportedSchemaVersion,
                $"File has schema {settings.SchemaVersion}, this build supports up to {AppSettings.CurrentSchemaVersion}");
            _logger.LogError("Settings schema {FileSchema} is newer than supported {SupportedSchema}",
                settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
            return new SettingsLoadResult(null, failure);
        }

        settings = MigrateToCurrentSchema(settings, json);
        if (settings is null)
        {
            return new SettingsLoadResult(null, PreserveInvalidFile("Schema 1 section could not be read for migration"));
        }

        return new SettingsLoadResult(settings, null);
    }

    /// <summary>
    /// Runs schema migrations in memory; the next save persists the current
    /// schema and re-migrating on load is idempotent. Schema 1 kept one flat
    /// configuration (top-level Windows/Sonar), which the current model no
    /// longer deserializes - so a v1-shaped file (explicit version 1, or no
    /// version field at all and no profiles) is re-read with the v1 shape
    /// before migrating. Guarding on an empty profile list keeps a v2 file
    /// whose version field was hand-edited away from being flattened.
    /// Returns null when the v1 re-read fails (practically unreachable: the
    /// document already parsed as the current model); the caller preserves
    /// the file for diagnosis instead of silently dropping captured state.
    /// </summary>
    private AppSettings? MigrateToCurrentSchema(AppSettings settings, string json)
    {
        if (settings.SchemaVersion != 1)
        {
            bool hasExplicitSchemaVersion = json.Contains("SchemaVersion", StringComparison.OrdinalIgnoreCase);
            if (hasExplicitSchemaVersion || settings.Profiles.Count > 0)
            {
                return settings;
            }
        }

        try
        {
            AppSettingsV1? legacy = JsonSerializer.Deserialize<AppSettingsV1>(json, SerializerOptions);
            if (legacy is null)
            {
                _logger.LogError("Schema 1 settings deserialized to null during migration");
                return null;
            }

            AppSettings migrated = AppSettingsMigrations.V1ToV2(legacy);
            _logger.LogInformation("Migrated settings from schema {From} to schema {To}",
                legacy.SchemaVersion, AppSettings.CurrentSchemaVersion);
            return migrated;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Schema 1 settings could not be read for migration");
            return null;
        }
    }

    /// <summary>Saves settings atomically through a temp file and replace.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        Directory.CreateDirectory(_settingsDirectory);

        string tempPath = _settingsPath + ".tmp";
        string json = JsonSerializer.Serialize(settings, SerializerOptions);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        // File.Replace is atomic on NTFS; Move with overwrite covers the
        // first-save case where no target exists yet.
        if (File.Exists(_settingsPath))
        {
            string backupPath = _settingsPath + ".bak";
            File.Replace(tempPath, _settingsPath, backupPath);
            File.Delete(backupPath);
        }
        else
        {
            File.Move(tempPath, _settingsPath);
        }

        _logger.LogInformation("Settings saved to {Path}", _settingsPath);

        // Raised only after the file is durably replaced, so subscribers
        // always observe persisted state.
        Saved?.Invoke(settings);
    }

    private SettingsLoadFailure PreserveInvalidFile(string detail)
    {
        _logger.LogError("Settings file is invalid and will be preserved for diagnosis: {Path} ({Detail})",
            _settingsPath, detail);

        try
        {
            string preservedPath = Path.Combine(
                _settingsDirectory,
                $"settings.invalid-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            File.Move(_settingsPath, preservedPath);
            _logger.LogWarning("Invalid settings preserved as {Path}", preservedPath);
        }
        catch (IOException ex)
        {
            // Losing the preserved copy is not fatal; the user still gets a
            // fresh settings file and the original text stays in the log.
            _logger.LogWarning(ex, "Could not preserve invalid settings file");
        }

        return new SettingsLoadFailure(SettingsLoadProblem.InvalidJson, detail);
    }
}
