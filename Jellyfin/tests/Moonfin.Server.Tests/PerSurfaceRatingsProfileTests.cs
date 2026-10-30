using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Moonfin.Server.Api;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Home rows and the media bar carry their own copy of the ratings settings, so each one has to
/// survive the profile JSON, the admin defaults XML, profile resolution and the ratings filter.
/// </summary>
public sealed class PerSurfaceRatingsProfileTests
{
    private static MoonfinSettingsProfile AllSet() => new()
    {
        HomeRowsMdblistEnabled = true,
        HomeRowsMdblistRatingSources = ["imdb", "letterboxd"],
        HomeRowsTmdbEpisodeRatingsEnabled = false,
        HomeRowsMdblistShowRatingNames = false,
        HomeRowsMdblistShowRatingBadges = true,
        MediaBarMdblistEnabled = false,
        MediaBarMdblistRatingSources = ["tomatoes"],
        MediaBarMdblistShowRatingNames = true,
        MediaBarMdblistShowRatingBadges = false,
    };

    private static void AssertCarriesAllSet(MoonfinSettingsProfile profile)
    {
        var json = JsonSerializer.Serialize(profile);

        Assert.Contains("\"homeRowsMdblistEnabled\":true", json);
        Assert.Contains("\"homeRowsMdblistRatingSources\":[\"imdb\",\"letterboxd\"]", json);
        Assert.Contains("\"homeRowsTmdbEpisodeRatingsEnabled\":false", json);
        Assert.Contains("\"homeRowsMdblistShowRatingNames\":false", json);
        Assert.Contains("\"homeRowsMdblistShowRatingBadges\":true", json);
        Assert.Contains("\"mediaBarMdblistEnabled\":false", json);
        Assert.Contains("\"mediaBarMdblistRatingSources\":[\"tomatoes\"]", json);
        Assert.Contains("\"mediaBarMdblistShowRatingNames\":true", json);
        Assert.Contains("\"mediaBarMdblistShowRatingBadges\":false", json);
    }

    [Fact]
    public void TheFieldsUseTheClientsWireNames() => AssertCarriesAllSet(AllSet());

    [Fact]
    public void AdminDefaultsSurviveTheConfigXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, new PluginConfiguration { DefaultUserSettings = AllSet() });
        stream.Position = 0;
        using var reader = XmlReader.Create(stream);
        var back = ((PluginConfiguration)serializer.Deserialize(reader)!).DefaultUserSettings!;

        AssertCarriesAllSet(back);
    }

    [Fact]
    public void ResolvingPrefersTheDeviceAndNormalizesTheLegacyAudienceId()
    {
        var service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), Path.GetTempPath());
        var settings = new MoonfinUserSettings
        {
            Global = new MoonfinSettingsProfile
            {
                HomeRowsMdblistShowRatingNames = true,
                HomeRowsMdblistRatingSources = ["rtAudience", "imdb"],
                MediaBarMdblistRatingSources = ["RTAUDIENCE"],
            },
            Tv = new MoonfinSettingsProfile { HomeRowsMdblistShowRatingNames = false },
        };

        var resolved = service.ResolveProfile(settings, "tv");

        Assert.False(resolved.HomeRowsMdblistShowRatingNames);
        Assert.Equal(["tomatoes_audience", "imdb"], resolved.HomeRowsMdblistRatingSources!);
        Assert.Equal(["tomatoes_audience"], resolved.MediaBarMdblistRatingSources!);
    }

    [Fact]
    public void TheRatingsFilterAlsoKeepsSourcesOnlyTheSurfaceListsAskFor()
    {
        var resolved = new MoonfinSettingsProfile
        {
            MdblistRatingSources = ["tomatoes", "imdb"],
            HomeRowsMdblistRatingSources = ["IMDB", "letterboxd", ""],
            MediaBarMdblistRatingSources = ["letterboxd", "trakt"],
        };

        Assert.Equal(["tomatoes", "imdb", "letterboxd", "trakt"], MdbListController.RequestedRatingSources(resolved));
    }

    [Fact]
    public void WithoutSurfaceListsTheFilterIsWhatItWasBefore()
    {
        Assert.Equal(["tomatoes"], MdbListController.RequestedRatingSources(new MoonfinSettingsProfile { MdblistRatingSources = ["tomatoes"] }));
        Assert.Equal(["imdb", "tmdb", "tomatoes", "metacritic"], MdbListController.RequestedRatingSources(new MoonfinSettingsProfile()));
        Assert.Equal(["imdb", "tmdb", "tomatoes", "metacritic"], MdbListController.RequestedRatingSources(null));
    }
}
