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
    public async Task Save_RaisesSavedEventWithSavedSnapshot()
    {
        AppSettings? received = null;
        _service.Saved += settings => received = settings;

        var settings = new AppSettings { AutoRestoreEnabled = false };
        await _service.SaveAsync(settings);

        // Subscribers keep UI toggle state in sync with what was persisted;
        // they must receive the exact saved instance, after the write.
        Assert.Same(settings, received);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var settings = new AppSettings
        {
            AutoRestoreEnabled = false,
            StartWithWindows = true,
            ActiveProfileId = ProfileIds.Speakers,
            Profiles =
            {
                [ProfileIds.Headphones] = new ProfileSettings
                {
                    Name = "Headphones",
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
                },
                [ProfileIds.Speakers] = new ProfileSettings { Name = "Speakers" },
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
        Assert.Equal(ProfileIds.Speakers, loaded.ActiveProfileId);
        Assert.Equal(2, loaded.Profiles.Count);

        var headphones = loaded.Profiles[ProfileIds.Headphones];
        Assert.Equal("render-a", headphones.Windows!.PlaybackConsoleId);
        Assert.Equal("render-b", headphones.Windows!.PlaybackCommunicationsId);
        Assert.Equal("capture-a", headphones.Windows!.RecordingCommunicationsId);
        Assert.Equal("sonar-game", headphones.Sonar!.Channels["Game"]);

        Assert.Equal("Speakers (Realtek)", loaded.DeviceNames["render-a"]);
        // The active profile (Speakers) is empty, so nothing is locked.
        Assert.False(loaded.HasLockedState);
    }

    [Fact]
    public async Task Load_SchemaV1File_MigratesToProfiles()
    {
        string path = Path.Combine(_directory, "settings.json");
        const string json = """
            {
              "SchemaVersion": 1,
              "AutoRestoreEnabled": false,
              "StartWithWindows": true,
              "Windows": {
                "PlaybackConsoleId": "render-a",
                "PlaybackMultimediaId": "render-a",
                "PlaybackCommunicationsId": "render-b",
                "RecordingCommunicationsId": "capture-a"
              },
              "Sonar": {
                "Channels": { "Game": "sonar-game", "Mic": "sonar-mic" }
              },
              "DeviceNames": { "render-a": "Speakers (Realtek)" }
            }
            """;
        await File.WriteAllTextAsync(path, json);

        var result = await _service.LoadAsync();

        var loaded = result.Settings;
        Assert.NotNull(loaded);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded!.SchemaVersion);
        // The pre-upgrade configuration stays active, so enforcement is
        // unchanged across the upgrade; only the label is new.
        Assert.Equal(ProfileIds.Headphones, loaded.ActiveProfileId);
        Assert.Equal(2, loaded.Profiles.Count);

        var headphones = loaded.Profiles[ProfileIds.Headphones];
        Assert.Equal("Headphones", headphones.Name);
        Assert.Equal("render-a", headphones.Windows!.PlaybackConsoleId);
        Assert.Equal("render-b", headphones.Windows!.PlaybackCommunicationsId);
        Assert.Equal("capture-a", headphones.Windows!.RecordingCommunicationsId);
        Assert.Equal("sonar-game", headphones.Sonar!.Channels["Game"]);

        var speakers = loaded.Profiles[ProfileIds.Speakers];
        Assert.Equal("Speakers", speakers.Name);
        Assert.Null(speakers.Windows);
        Assert.Null(speakers.Sonar);

        Assert.False(loaded.AutoRestoreEnabled);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("Speakers (Realtek)", loaded.DeviceNames["render-a"]);
        Assert.True(loaded.HasLockedState);
    }

    [Fact]
    public async Task Load_SchemaV1FileWithoutVersionField_MigratesInsteadOfDroppingState()
    {
        // A hand-edited file that lost its SchemaVersion must not have its
        // flat Windows/Sonar state silently dropped.
        string path = Path.Combine(_directory, "settings.json");
        const string json = """
            {
              "AutoRestoreEnabled": true,
              "Windows": { "PlaybackConsoleId": "render-a", "PlaybackMultimediaId": "render-a" }
            }
            """;
        await File.WriteAllTextAsync(path, json);

        var result = await _service.LoadAsync();

        var loaded = result.Settings;
        Assert.NotNull(loaded);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded!.SchemaVersion);
        Assert.Equal("render-a", loaded.Profiles[ProfileIds.Headphones].Windows!.PlaybackConsoleId);
        Assert.True(loaded.HasLockedState);
    }

    [Fact]
    public async Task Load_SchemaV2WithProfilesAndStrippedVersion_IsNotFlattened()
    {
        string path = Path.Combine(_directory, "settings.json");
        const string json = """
            {
              "AutoRestoreEnabled": true,
              "ActiveProfileId": "speakers",
              "Profiles": {
                "headphones": { "Name": "Headphones", "Windows": { "PlaybackConsoleId": "render-a" } },
                "speakers": { "Name": "Speakers" }
              }
            }
            """;
        await File.WriteAllTextAsync(path, json);

        var result = await _service.LoadAsync();

        var loaded = result.Settings;
        Assert.NotNull(loaded);
        Assert.Equal(ProfileIds.Speakers, loaded!.ActiveProfileId);
        Assert.Equal("render-a", loaded.Profiles[ProfileIds.Headphones].Windows!.PlaybackConsoleId);
        Assert.Null(loaded.Profiles[ProfileIds.Speakers].Windows);
    }

    [Fact]
    public async Task Load_SchemaV2WithoutProfiles_IsLeftUnmigrated()
    {
        // A fresh v2 save before any capture has no profiles and no flat
        // state; nothing to migrate and nothing to lose.
        string path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, """{ "SchemaVersion": 2, "AutoRestoreEnabled": false }""");

        var result = await _service.LoadAsync();

        var loaded = result.Settings;
        Assert.NotNull(loaded);
        Assert.False(loaded!.AutoRestoreEnabled);
        Assert.Empty(loaded.Profiles);
        Assert.False(loaded.HasLockedState);
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
