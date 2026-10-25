using System.Text.Json;
using Emby.Plugins.Moonfin.Models;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The settings envelope decides which profile a client is handed and whether an old file needs
/// migrating.
/// </summary>
public class MoonfinUserSettingsTests
{
    private static MoonfinUserSettings Parse(string json) =>
        JsonSerializer.Deserialize<MoonfinUserSettings>(json)!;

    [Theory]
    [InlineData("desktop")]
    [InlineData("Desktop")]
    [InlineData("DESKTOP")]
    public void GetProfile_MatchesTheNameWhateverItsCasing(string name)
    {
        var profile = new MoonfinSettingsProfile();
        var settings = new MoonfinUserSettings { Desktop = profile };

        Assert.Same(profile, settings.GetProfile(name));
    }

    [Fact]
    public void GetProfile_ReachesEveryDeviceProfile()
    {
        var settings = new MoonfinUserSettings
        {
            Desktop = new MoonfinSettingsProfile(),
            Mobile = new MoonfinSettingsProfile(),
            Tv = new MoonfinSettingsProfile(),
        };

        Assert.Same(settings.Desktop, settings.GetProfile("desktop"));
        Assert.Same(settings.Mobile, settings.GetProfile("mobile"));
        Assert.Same(settings.Tv, settings.GetProfile("tv"));
    }

    [Fact]
    public void GetProfile_AnswersNullForGlobalEvenThoughSetProfileWritesIt()
    {
        // The pair is deliberately asymmetric: global is not a device profile to resolve against,
        // so only the setter takes it.
        var settings = new MoonfinUserSettings();
        settings.SetProfile("global", new MoonfinSettingsProfile());

        Assert.NotNull(settings.Global);
        Assert.Null(settings.GetProfile("global"));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("")]
    public void GetProfile_AnswersNullForAnUnknownName(string name)
    {
        Assert.Null(new MoonfinUserSettings { Desktop = new MoonfinSettingsProfile() }.GetProfile(name));
    }

    [Fact]
    public void SetProfile_WritesEverySlotAndIgnoresAnUnknownName()
    {
        var settings = new MoonfinUserSettings();

        settings.SetProfile("Global", new MoonfinSettingsProfile());
        settings.SetProfile("MOBILE", new MoonfinSettingsProfile());
        settings.SetProfile("phone", new MoonfinSettingsProfile());

        Assert.NotNull(settings.Global);
        Assert.NotNull(settings.Mobile);
        Assert.Null(settings.Desktop);
        Assert.Null(settings.Tv);
    }

    [Fact]
    public void SetProfile_ClearsASlotWhenHandedNull()
    {
        var settings = new MoonfinUserSettings { Tv = new MoonfinSettingsProfile() };

        settings.SetProfile("tv", null);

        Assert.Null(settings.Tv);
    }

    [Fact]
    public void ValidProfiles_ListsTheFourSlotsSetProfileAccepts()
    {
        Assert.Equal(new[] { "global", "desktop", "mobile", "tv" }, MoonfinUserSettings.ValidProfiles);
    }

    [Theory]
    [InlineData("navbarEnabled", "true")]
    [InlineData("mediaBarEnabled", "false")]
    [InlineData("mdblistEnabled", "true")]
    [InlineData("seerrEnabled", "true")]
    [InlineData("tmdbEpisodeRatingsEnabled", "true")]
    [InlineData("navbarPosition", "\"bottom\"")]
    [InlineData("detailsPageEnabled", "true")]
    public void NeedsMigration_IsTrueForAV1FileCarryingAnyFlatSetting(string key, string value)
    {
        var settings = Parse($"{{\"schemaVersion\":1,\"{key}\":{value}}}");

        Assert.True(settings.NeedsMigration);
    }

    [Fact]
    public void NeedsMigration_IsFalseOnceTheFileHasBeenMigrated()
    {
        Assert.False(Parse("{\"schemaVersion\":2,\"navbarEnabled\":true}").NeedsMigration);
        Assert.False(Parse("{\"schemaVersion\":1,\"navbarEnabled\":true,\"global\":{}}").NeedsMigration);
        Assert.False(Parse("{\"schemaVersion\":1}").NeedsMigration);
    }

    [Fact]
    public void AFreshEnvelopeDefaultsToTheCurrentSchemaWithSyncOn()
    {
        var settings = new MoonfinUserSettings();

        Assert.Equal(2, settings.SchemaVersion);
        Assert.True(settings.SyncEnabled);
        Assert.False(settings.NeedsMigration);
    }
}
