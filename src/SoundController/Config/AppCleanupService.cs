using System.IO;
using Microsoft.Extensions.Logging;
using SoundController.Autostart;

namespace SoundController.Config;

/// <summary>
/// What the uninstall cleanup managed to remove. <see cref="Errors"/> lists
/// anything the user must delete manually, with the exact path.
/// </summary>
public sealed record CleanupResult(bool AutostartRemoved, bool DataFolderDeleted, IReadOnlyList<string> Errors)
{
    public bool FullyClean => AutostartRemoved && DataFolderDeleted && Errors.Count == 0;
}

/// <summary>
/// Removes everything this application has created outside itself: the HKCU
/// autostart value and the per-user data folder (settings + logs). The app
/// registers no COM components and writes no other registry entries, so
/// these two steps are the complete removal story.
/// </summary>
public sealed class AppCleanupService
{
    private readonly string _dataDirectory;
    private readonly IAutostartService _autostart;
    private readonly ILogger<AppCleanupService> _logger;

    public AppCleanupService(string dataDirectory, IAutostartService autostart, ILogger<AppCleanupService> logger)
    {
        _dataDirectory = dataDirectory;
        _autostart = autostart;
        _logger = logger;
    }

    /// <summary>
    /// Best-effort removal; never throws. Individual failures are collected
    /// in the returned result so the UI can tell the user exactly what to
    /// remove manually. Callers must stop services and release file handles
    /// (logs, settings) first, or the folder deletion will fail on Windows.
    /// </summary>
    public CleanupResult RemoveAllAppData()
    {
        var errors = new List<string>();

        bool autostartRemoved = RemoveAutostart(errors);
        bool dataDeleted = RemoveDataFolder(errors);

        _logger.LogInformation(
            "Cleanup finished: autostart removed = {Autostart}, data folder deleted = {Data}, errors = {ErrorCount}",
            autostartRemoved, dataDeleted, errors.Count);

        return new CleanupResult(autostartRemoved, dataDeleted, errors);
    }

    private bool RemoveAutostart(List<string> errors)
    {
        try
        {
            _autostart.SetEnabled(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup: removing autostart entry failed");
            errors.Add(
                @"Registry autostart value HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SoundController could not be removed. Remove it manually with regedit.");
            return false;
        }
    }

    private bool RemoveDataFolder(List<string> errors)
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }

            // An already-absent folder counts as clean.
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup: deleting data folder failed");
            errors.Add(
                $"Data folder {_dataDirectory} could not be fully deleted (a file may still be in use). Delete it manually once the application has exited.");
            return false;
        }
    }
}
