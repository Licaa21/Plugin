using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Home rows and the media bar carry their own copy of the ratings settings, so each one has to
/// survive the profile JSON, the admin defaults XML and the ratings filter.
/// </summary>
public class PerSurfaceRatingsProfileTests
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
    public void TheRatingsFilterAlsoKeepsSourcesOnlyTheSurfaceListsAskFor()
    {
        var resolved = new MoonfinSettingsProfile
        {
            MdblistRatingSources = ["tomatoes", "imdb"],
            HomeRowsMdblistRatingSources = ["IMDB", "letterboxd", ""],
            MediaBarMdblistRatingSources = ["letterboxd", "trakt"],
        };

        Assert.Equal(["tomatoes", "imdb", "letterboxd", "trakt"], RatingsService.RequestedRatingSources(resolved));
    }

    [Fact]
    public void WithoutSurfaceListsTheFilterIsWhatItWasBefore()
    {
        Assert.Equal(["tomatoes"], RatingsService.RequestedRatingSources(new MoonfinSettingsProfile { MdblistRatingSources = ["tomatoes"] }));
        Assert.Equal(["imdb", "tmdb", "tomatoes", "metacritic"], RatingsService.RequestedRatingSources(new MoonfinSettingsProfile()));
        Assert.Equal(["imdb", "tmdb", "tomatoes", "metacritic"], RatingsService.RequestedRatingSources(null));
    }
}
