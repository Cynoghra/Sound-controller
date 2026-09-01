using SteelSeriesAPI.Sonar.Enums;
using SoundController.Config;
using SoundController.Core;
using SoundController.Sonar;
using Xunit;

namespace SoundController.Tests;

/// <summary>
/// Tests for the pure restore decision logic. No audio hardware, GG, or
/// Sonar connection involved - snapshots are constructed by hand.
/// </summary>
public class RestoreEngineTests
{
    private const string Speakers = "{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}";
    private const string Headset = "{0.0.0.00000000}.{22222222-2222-2222-2222-222222222222}";
    private const string Controller = "{0.0.0.00000000}.{33333333-3333-3333-3333-333333333333}";
    private const string Mic = "{0.0.1.00000000}.{44444444-4444-4444-4444-444444444444}";

    private static AppSettings LockedSettings(
        Dictionary<Channel, string>? sonar = null,
        WindowsDefaultsSettings? windows = null,
        string activeProfileId = ProfileIds.Headphones)
    {
        var settings = new AppSettings { ActiveProfileId = activeProfileId };
        settings.Profiles[activeProfileId] = new ProfileSettings
        {
            Name = ProfileIds.DisplayNameFor(activeProfileId),
            Sonar = sonar is null
                ? null
                : new SonarDefaultsSettings
                {
                    Channels = sonar.ToDictionary(kvp => kvp.Key.ToString(), kvp => (string?)kvp.Value),
                },
            Windows = windows,
        };
        return settings;
    }

    private static SonarSnapshot SonarSnapshot(
        Dictionary<Channel, string?> devices,
        IEnumerable<string>? knownIds = null)
    {
        var known = new HashSet<string>(knownIds ?? devices.Values.Where(v => v is not null).Select(v => v!),
            StringComparer.OrdinalIgnoreCase);
        return new SonarSnapshot(devices, known);
    }

    private static WindowsSnapshot WindowsSnapshot(
        string? renderConsole = null,
        string? renderMultimedia = null,
        string? renderCommunications = null,
        string? captureConsole = null,
        string? captureMultimedia = null,
        string? captureCommunications = null,
        string[]? availableRender = null,
        string[]? availableCapture = null)
    {
        var defaults = new Dictionary<AudioRoleKey, string?>
        {
            [new AudioRoleKey(AudioDirection.Render, DefaultRole.Console)] = renderConsole,
            [new AudioRoleKey(AudioDirection.Render, DefaultRole.Multimedia)] = renderMultimedia,
            [new AudioRoleKey(AudioDirection.Render, DefaultRole.Communications)] = renderCommunications,
            [new AudioRoleKey(AudioDirection.Capture, DefaultRole.Console)] = captureConsole,
            [new AudioRoleKey(AudioDirection.Capture, DefaultRole.Multimedia)] = captureMultimedia,
            [new AudioRoleKey(AudioDirection.Capture, DefaultRole.Communications)] = captureCommunications,
        };
        return new WindowsSnapshot(
            defaults,
            new HashSet<string>(availableRender ?? Array.Empty<string>()),
            new HashSet<string>(availableCapture ?? Array.Empty<string>()));
    }

    [Fact]
    public void Plan_MatchingState_ProducesEmptyPlan()
    {
        var settings = LockedSettings(
            sonar: new Dictionary<Channel, string> { [Channel.Game] = Speakers, [Channel.Mic] = Mic },
            windows: new WindowsDefaultsSettings
            {
                PlaybackConsoleId = Speakers,
                PlaybackMultimediaId = Speakers,
                PlaybackCommunicationsId = Headset,
            });

        var sonar = SonarSnapshot(new Dictionary<Channel, string?>
        {
            [Channel.Game] = Speakers,
            [Channel.Chat] = Headset,
            [Channel.Mic] = Mic,
        });
        var windows = WindowsSnapshot(
            renderConsole: Speakers,
            renderMultimedia: Speakers,
            renderCommunications: Headset,
            availableRender: new[] { Speakers, Headset, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, windows);

        Assert.True(plan.RequiresNoWrites);
        Assert.Empty(plan.Unavailable);
    }

    [Fact]
    public void Plan_SonarDrift_ProducesOnlySonarCorrection()
    {
        // The PS5 controller has stolen the Game channel.
        var settings = LockedSettings(sonar: new Dictionary<Channel, string> { [Channel.Game] = Speakers });
        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Controller },
            knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, windows: null);

        var correction = Assert.Single(plan.SonarCorrections);
        Assert.Equal(Channel.Game, correction.Channel);
        Assert.Equal(Controller, correction.CurrentDeviceId);
        Assert.Equal(Speakers, correction.DesiredDeviceId);
        Assert.Empty(plan.WindowsCorrections);
    }

    [Fact]
    public void Plan_WindowsDrift_ProducesOnlyWindowsCorrection()
    {
        var settings = LockedSettings(windows: new WindowsDefaultsSettings
        {
            PlaybackConsoleId = Speakers,
            PlaybackMultimediaId = Speakers,
            PlaybackCommunicationsId = Headset,
        });
        var windows = WindowsSnapshot(
            renderConsole: Controller,
            renderMultimedia: Speakers,
            renderCommunications: Headset,
            availableRender: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, null, windows);

        var correction = Assert.Single(plan.WindowsCorrections);
        Assert.Equal(new AudioRoleKey(AudioDirection.Render, DefaultRole.Console), correction.Role);
        Assert.Equal(Speakers, correction.DesiredDeviceId);
    }

    [Fact]
    public void Plan_MissingLockedDevice_IsReportedAndNotApplied()
    {
        // The saved headset is unplugged: no correction, but reported.
        var settings = LockedSettings(sonar: new Dictionary<Channel, string> { [Channel.Game] = Headset });
        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Controller },
            knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        Assert.Empty(plan.SonarCorrections);
        var unavailable = Assert.Single(plan.Unavailable);
        Assert.Equal(Headset, unavailable.DeviceId);
        Assert.Contains("Game", unavailable.Context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_MissingWindowsDevice_IsReportedAndNotApplied()
    {
        var settings = LockedSettings(windows: new WindowsDefaultsSettings
        {
            PlaybackConsoleId = Headset,
            PlaybackMultimediaId = Headset,
        });
        var windows = WindowsSnapshot(
            renderConsole: Speakers,
            renderMultimedia: Speakers,
            availableRender: new[] { Speakers });

        var plan = RestoreEngine.Plan(settings, null, windows);

        Assert.Empty(plan.WindowsCorrections);
        var unavailable = Assert.Single(plan.Unavailable);
        Assert.Equal(Headset, unavailable.DeviceId);
    }

    [Fact]
    public void Plan_NullLockedEntries_AreLeftAlone()
    {
        // A role the user never locked must never be touched, even when it drifts.
        var settings = LockedSettings(windows: new WindowsDefaultsSettings
        {
            PlaybackConsoleId = Speakers,
            PlaybackMultimediaId = Speakers,
        });
        var windows = WindowsSnapshot(
            renderConsole: Speakers,
            renderMultimedia: Speakers,
            renderCommunications: Controller, // drifted but not locked
            availableRender: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, null, windows);

        Assert.True(plan.RequiresNoWrites);
        Assert.Empty(plan.Unavailable);
    }

    [Fact]
    public void Plan_NullSnapshot_SkipsDomain()
    {
        // Sonar down must not block the Windows restore.
        var settings = LockedSettings(
            sonar: new Dictionary<Channel, string> { [Channel.Game] = Speakers },
            windows: new WindowsDefaultsSettings { PlaybackConsoleId = Speakers, PlaybackMultimediaId = Speakers });

        var windows = WindowsSnapshot(
            renderConsole: Controller,
            renderMultimedia: Speakers,
            availableRender: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, null, windows);

        Assert.Empty(plan.SonarCorrections);
        Assert.Single(plan.WindowsCorrections);
    }

    [Fact]
    public void Plan_UnknownChannelName_IsSkipped()
    {
        var settings = new AppSettings { ActiveProfileId = ProfileIds.Headphones };
        settings.Profiles[ProfileIds.Headphones] = new ProfileSettings
        {
            Name = "Headphones",
            Sonar = new SonarDefaultsSettings
            {
                Channels = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["FutureChannel"] = Speakers, // added by a later GG version
                    ["Game"] = Speakers,
                },
            },
        };
        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Controller },
            knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        Assert.Single(plan.SonarCorrections);
        Assert.Empty(plan.Unavailable);
    }

    [Fact]
    public void Plan_SonarChannelMissingFromSnapshot_IsLeftAlone()
    {
        // Fresh GG install may not report a channel yet; guessing would be wrong.
        var settings = LockedSettings(sonar: new Dictionary<Channel, string> { [Channel.Aux] = Speakers });
        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Speakers },
            knownIds: new[] { Speakers });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        Assert.True(plan.RequiresNoWrites);
    }

    [Fact]
    public void Plan_MasterChannel_IsNeverPlanned()
    {
        // Regression: Master is a mixer concept, not a redirection target.
        // Writing it via SetClassicDevice is rejected by Sonar's backend and
        // was observed to leave channels unchosen in the UI.
        var settings = new AppSettings { ActiveProfileId = ProfileIds.Headphones };
        settings.Profiles[ProfileIds.Headphones] = new ProfileSettings
        {
            Name = "Headphones",
            Sonar = new SonarDefaultsSettings
            {
                Channels = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Master"] = Speakers,
                    ["Game"] = Speakers,
                },
            },
        };
        var sonar = SonarSnapshot(new Dictionary<Channel, string?>
        {
            [Channel.Master] = Controller,
            [Channel.Game] = Controller,
        }, knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        var correction = Assert.Single(plan.SonarCorrections);
        Assert.Equal(Channel.Game, correction.Channel);
        Assert.Empty(plan.Unavailable);
    }

    [Fact]
    public void Plan_ClearedLockedChannel_IsHealed()
    {
        // Regression: Sonar sometimes clears redirections ("red, no device")
        // after backend churn. An explicitly empty redirection on a locked
        // channel must be healed by writing the locked device back.
        var settings = LockedSettings(sonar: new Dictionary<Channel, string> { [Channel.Game] = Speakers });
        var sonar = SonarSnapshot(new Dictionary<Channel, string?>
        {
            [Channel.Game] = string.Empty,
            [Channel.Chat] = Headset,
        }, knownIds: new[] { Speakers, Headset });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        var correction = Assert.Single(plan.SonarCorrections);
        Assert.Equal(Channel.Game, correction.Channel);
        Assert.Equal(string.Empty, correction.CurrentDeviceId);
        Assert.Equal(Speakers, correction.DesiredDeviceId);
    }

    [Fact]
    public void Plan_MicWithClearedRedirection_IsHealed()
    {
        var settings = LockedSettings(sonar: new Dictionary<Channel, string> { [Channel.Mic] = Mic });
        var sonar = SonarSnapshot(new Dictionary<Channel, string?>
        {
            [Channel.Mic] = null,
        }, knownIds: new[] { Mic });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        var correction = Assert.Single(plan.SonarCorrections);
        Assert.Equal(Channel.Mic, correction.Channel);
        Assert.Equal(Mic, correction.DesiredDeviceId);
    }

    [Fact]
    public void Plan_UsesActiveProfile_IgnoresInactiveProfile()
    {
        // Headphones locks Game -> Speakers; Speakers locks Game -> Controller.
        // Only the active profile's lock may produce a correction.
        var settings = LockedSettings(
            sonar: new Dictionary<Channel, string> { [Channel.Game] = Speakers },
            activeProfileId: ProfileIds.Headphones);
        settings.Profiles[ProfileIds.Speakers] = new ProfileSettings
        {
            Name = "Speakers",
            Sonar = new SonarDefaultsSettings
            {
                Channels = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Game"] = Controller },
            },
        };

        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Controller },
            knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        var correction = Assert.Single(plan.SonarCorrections);
        Assert.Equal(Speakers, correction.DesiredDeviceId);
    }

    [Fact]
    public void Plan_UnknownActiveProfileId_ProducesEmptyPlan()
    {
        // A hand-edited file must not make the engine write anything; the
        // coordinator surfaces the situation through status instead.
        var settings = new AppSettings
        {
            ActiveProfileId = "bogus-profile",
            Profiles =
            {
                [ProfileIds.Headphones] = new ProfileSettings
                {
                    Name = "Headphones",
                    Sonar = new SonarDefaultsSettings
                    {
                        Channels = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Game"] = Speakers },
                    },
                },
            },
        };
        var sonar = SonarSnapshot(new Dictionary<Channel, string?> { [Channel.Game] = Controller },
            knownIds: new[] { Speakers, Controller });

        var plan = RestoreEngine.Plan(settings, sonar, null);

        Assert.True(plan.RequiresNoWrites);
        Assert.Empty(plan.Unavailable);
    }

    [Fact]
    public void HasLockedState_ReflectsActiveProfileOnly()
    {
        var emptyActive = new AppSettings { ActiveProfileId = ProfileIds.Speakers };
        emptyActive.Profiles[ProfileIds.Speakers] = new ProfileSettings { Name = "Speakers" };
        emptyActive.Profiles[ProfileIds.Headphones] = new ProfileSettings
        {
            Name = "Headphones",
            Windows = new WindowsDefaultsSettings { PlaybackConsoleId = Speakers, PlaybackMultimediaId = Speakers },
        };
        Assert.False(emptyActive.HasLockedState);

        var capturedActive = new AppSettings { ActiveProfileId = ProfileIds.Speakers };
        capturedActive.Profiles[ProfileIds.Speakers] = new ProfileSettings
        {
            Name = "Speakers",
            Sonar = new SonarDefaultsSettings
            {
                Channels = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Game"] = Speakers },
            },
        };
        Assert.True(capturedActive.HasLockedState);
    }
}
