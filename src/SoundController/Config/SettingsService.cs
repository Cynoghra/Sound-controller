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

        // Future migrations go here, each bumping settings.SchemaVersion forward.

        return new SettingsLoadResult(settings, null);
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
