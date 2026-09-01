using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SoundController.Autostart;

/// <summary>Controls the current-user autostart entry (HKCU Run key).</summary>
public interface IAutostartService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>
/// Autostart via HKCU\...\Run. Uses the current-user hive so no elevation is
/// required. The registry value is the source of truth; the settings file
/// only caches the last known state for UI display.
/// </summary>
public sealed class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundController";

    private readonly ILogger<AutostartService> _logger;

    public AutostartService(ILogger<AutostartService> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            // Quote the path: the repo/install location may contain spaces.
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path for autostart");
            key.SetValue(ValueName, $"\"{exePath}\"");
            _logger.LogInformation("Autostart enabled for {ExePath}", exePath);
        }
        else
        {
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
                _logger.LogInformation("Autostart disabled");
            }
        }
    }
}
