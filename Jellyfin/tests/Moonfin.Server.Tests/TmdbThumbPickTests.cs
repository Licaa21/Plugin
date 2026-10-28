using System.Text.Json;
using Moonfin.Server.Services;
using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class TmdbThumbPickTests
{
    [Fact]
    public void ABackdropWithTheTitleWins()
    {
        using var doc = JsonDocument.Parse("""
            {
              "backdrop_path": "/main.jpg",
              "images": {
                "backdrops": [
                  {"iso_639_1": null, "file_path": "/plain.jpg"},
                  {"iso_639_1": "en", "file_path": "/titled.jpg"}
                ]
              }
            }
            """);

        Assert.Equal("/titled.jpg", TmdbThumbService.PickPath(doc.RootElement, "en"));
    }

    [Fact]
    public void WithoutOneTheMainBackdropIsUsed()
    {
        using var doc = JsonDocument.Parse("""{"backdrop_path": "/main.jpg", "images": {"backdrops": [{"iso_639_1": null, "file_path": "/plain.jpg"}]}}""");

        Assert.Equal("/main.jpg", TmdbThumbService.PickPath(doc.RootElement, "en"));
    }

    [Fact]
    public void ATitleWithNoBackdropHasNoThumb()
    {
        using var doc = JsonDocument.Parse("""{"backdrop_path": null, "images": {"backdrops": []}}""");

        Assert.Null(TmdbThumbService.PickPath(doc.RootElement, "en"));
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void TheMetadataLanguageIsATwoLetterCode(string? configured, string expected)
    {
        Assert.Equal(expected, TmdbThumbService.MetadataLanguage(configured));
    }

    [Fact]
    public void AnItemFromAListTakesOnlyTheDetailsItIsMissing()
    {
        using var doc = JsonDocument.Parse("""{"overview": "A mob family.", "genres": [{"name": "Crime"}], "runtime": 175}""");
        var details = TmdbTitleDetails.Parse(doc.RootElement, "en");
        var bare = new CustomRowItem { Name = "The Godfather" };
        var described = new CustomRowItem { Name = "The Godfather", Overview = "From the list.", Genres = new List<string> { "Classic" } };

        TmdbThumbService.ApplyDetails(bare, details);
        TmdbThumbService.ApplyDetails(described, details);

        Assert.Equal("A mob family.", bare.Overview);
        Assert.Equal(new[] { "Crime" }, bare.Genres);
        Assert.Equal(175 * TimeSpan.TicksPerMinute, bare.RunTimeTicks);
        Assert.Equal("From the list.", described.Overview);
        Assert.Equal(new[] { "Classic" }, described.Genres);
    }
}
