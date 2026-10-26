using System.Text.Json;
using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Tmdb.Tests;

/// <summary>
/// A title from an outside list gets the same text, score, logo and trailer a library title
/// shows, read from one TMDB details call.
/// </summary>
public sealed class TmdbTitleDetailsTests
{
    private static TmdbTitleDetails Parse(string json, string language = "en")
    {
        using var doc = JsonDocument.Parse(json);
        return TmdbTitleDetails.Parse(doc.RootElement, language);
    }

    [Fact]
    public void ReadsAMovie()
    {
        var details = Parse("""
            {
              "overview": " A thief who steals secrets. ",
              "vote_average": 8.369, "vote_count": 36000,
              "genres": [{"id": 28, "name": "Action"}, {"id": 878, "name": "Science Fiction"}],
              "runtime": 148,
              "backdrop_path": "/main.jpg",
              "images": {
                "backdrops": [{"iso_639_1": "en", "file_path": "/titled.jpg"}, {"iso_639_1": null, "file_path": "/plain.jpg"}],
                "logos": [{"iso_639_1": "en", "file_path": "/logo.svg"}, {"iso_639_1": "en", "file_path": "/logo.png"}]
              },
              "videos": {"results": [
                {"site": "YouTube", "type": "Teaser", "key": "tease", "official": true},
                {"site": "Vimeo", "type": "Trailer", "key": "vimeo", "official": true},
                {"site": "YouTube", "type": "Trailer", "key": "fan", "official": false},
                {"site": "YouTube", "type": "Trailer", "key": "real", "official": true}
              ]}
            }
            """);

        Assert.Equal("A thief who steals secrets.", details.Overview);
        Assert.Equal(8.4, details.Rating);
        Assert.Equal(new[] { "Action", "Science Fiction" }, details.Genres);
        Assert.Equal(148L * 600_000_000, details.RunTimeTicks);
        Assert.Equal("/plain.jpg", details.BackdropPath);
        Assert.Equal("/logo.png", details.LogoPath);
        Assert.Equal("https://www.youtube.com/watch?v=real", details.TrailerUrl);
    }

    [Fact]
    public void ReadsAShowsEpisodeRuntime()
    {
        var details = Parse("""{"episode_run_time": [42, 60], "vote_count": 0, "vote_average": 0}""");

        Assert.Equal(42L * 600_000_000, details.RunTimeTicks);
        Assert.Null(details.Rating);
    }

    [Fact]
    public void PrefersTheAskedLanguageThenEnglishForTheLogo()
    {
        const string json = """
            {"images": {"logos": [
              {"iso_639_1": null, "file_path": "/none.png"},
              {"iso_639_1": "en", "file_path": "/en.png"},
              {"iso_639_1": "de", "file_path": "/de.png"}
            ]}}
            """;

        Assert.Equal("/de.png", Parse(json, "de").LogoPath);
        Assert.Equal("/en.png", Parse(json, "fr").LogoPath);
    }

    [Fact]
    public void FallsBackToTheMainBackdropWithoutATextlessOne()
    {
        var details = Parse("""
            {"overview": "", "backdrop_path": "/main.jpg",
             "images": {"backdrops": [{"iso_639_1": "en", "file_path": "/titled.jpg"}], "logos": []}}
            """);

        Assert.Equal("/main.jpg", details.BackdropPath);
        Assert.Null(details.Overview);
        Assert.Null(details.LogoPath);
        Assert.Null(details.TrailerUrl);
    }
}
