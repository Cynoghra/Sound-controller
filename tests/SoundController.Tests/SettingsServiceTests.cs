using Microsoft.Extensions.Logging.Abstractions;
using SoundController.Config;
using Xunit;

namespace SoundController.Tests;

/// <summary>
/// Settings persistence tests using a temp directory, so tests never touch the
/// developer's real %LOCALAPPDATA% state.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"soundcontroller-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _service = new SettingsService(_directory, NullLogger<SettingsService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failure is not a test failure.
        }
    }

    [Fact]
    public async Task Load_NoFile_ReturnsNotPresent()
    {
        var result = await _service.LoadAsync();

        Assert.Null(result.Settings);
        Assert.Equal(SettingsLoadProblem.NotPresent, result.Failure!.Problem);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var settings = new AppSettings
        {
            AutoRestoreEnabled = false,
            StartWithWindows = true,
            Windows = new WindowsDefaultsSettings
            {
                PlaybackConsoleId = "render-a",
                PlaybackMultimediaId = "render-a",
                PlaybackCommunicationsId = "render-b",
                RecordingCommunicationsId = "capture-a",
            },
            Sonar = new SonarDefaultsSettings
            {
                Channels = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Game"] = "sonar-game",
                    ["Mic"] = "sonar-mic",
                    ["Aux"] = null,
                },
            },
            DeviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["render-a"] = "Speakers (Realtek)",
            },
        };

        await _service.SaveAsync(settings);
        var result = await _service.LoadAsync();

        var loaded = result.Settings;
        Assert.NotNull(loaded);
        Assert.False(loaded!.AutoRestoreEnabled);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("render-a", loaded.Windows!.PlaybackConsoleId);
        Assert.Equal("render-b", loaded.Windows!.PlaybackCommunicationsId);
        Assert.Equal("capture-a", loaded.Windows!.RecordingCommunicationsId);
        Assert.Equal("sonar-game", loaded.Sonar!.Channels["Game"]);
        Assert.Equal("Speakers (Realtek)", loaded.DeviceNames["render-a"]);
        Assert.True(loaded.HasLockedState);
    }

    [Fact]
    public async Task Load_CorruptJson_PreservesFileAndReturnsNull()
    {
        string path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json");

        var result = await _service.LoadAsync();

        Assert.Null(result.Settings);
        Assert.Equal(SettingsLoadProblem.InvalidJson, result.Failure!.Problem);

        // The corrupt file must survive (renamed, not deleted) for diagnosis.
        string[] preserved = Directory.GetFiles(_directory, "settings.invalid-*.json");
        Assert.Single(preserved);
        Assert.Contains("this is not valid json", await File.ReadAllTextAsync(preserved[0]));
    }

    [Fact]
    public async Task Load_NewerSchemaVersion_IsRejectedLoudly()
    {
        string path = Path.Combine(_directory, "settings.json");
        string json = """
            { "SchemaVersion": 999, "AutoRestoreEnabled": true }
            """;
        await File.WriteAllTextAsync(path, json);

        var result = await _service.LoadAsync();

        Assert.Null(result.Settings);
        Assert.Equal(SettingsLoadProblem.UnsupportedSchemaVersion, result.Failure!.Problem);
        // The original file stays in place untouched.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Save_DoesNotLeaveTempFilesBehind()
    {
        var settings = new AppSettings();

        await _service.SaveAsync(settings);
        await _service.SaveAsync(settings); // exercises the File.Replace path

        string[] files = Directory.GetFiles(_directory);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_OverwritesPreviousContent()
    {
        var first = new AppSettings { AutoRestoreEnabled = true };
        var second = new AppSettings { AutoRestoreEnabled = false };

        await _service.SaveAsync(first);
        await _service.SaveAsync(second);

        var result = await _service.LoadAsync();
        Assert.NotNull(result.Settings);
        Assert.False(result.Settings!.AutoRestoreEnabled);
    }
}
