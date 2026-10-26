using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonfin.Tmdb;

/// <summary>
/// What an app shows for a movie or show the server doesn't have, the way it would for a library
/// title: the overview, score, genres, runtime, a backdrop without text, the logo and a trailer.
/// It all comes from one details call made with <see cref="Query"/>.
/// </summary>
public sealed class TmdbTitleDetails
{
    private const long TicksPerMinute = 600_000_000;

    [JsonPropertyName("overview")]
    public string? Overview { get; private set; }

    /// <summary>Gets the TMDB score out of 10, or null when nobody has voted.</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; private set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; private set; } = new List<string>();

    [JsonPropertyName("runTimeTicks")]
    public long? RunTimeTicks { get; private set; }

    [JsonPropertyName("backdropPath")]
    public string? BackdropPath { get; private set; }

    [JsonPropertyName("logoPath")]
    public string? LogoPath { get; private set; }

    [JsonPropertyName("trailerUrl")]
    public string? TrailerUrl { get; private set; }

    /// <summary>
    /// The query string for a details call in <paramref name="language"/>, a two letter code.
    /// English logos and trailers come along for titles with none in that language.
    /// </summary>
    public static string Query(string language) =>
        "language=" + language
        + "&append_to_response=images,videos"
        + "&include_image_language=" + language + ",en,null"
        + "&include_video_language=" + language + ",en,null";

    public static TmdbTitleDetails Parse(JsonElement details, string language)
    {
        var result = new TmdbTitleDetails();
        if (details.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var overview = StringOf(details, "overview");
        result.Overview = string.IsNullOrWhiteSpace(overview) ? null : overview!.Trim();

        if (details.TryGetProperty("vote_count", out var votes) && votes.ValueKind == JsonValueKind.Number && votes.GetInt32() > 0
            && details.TryGetProperty("vote_average", out var average) && average.ValueKind == JsonValueKind.Number)
        {
            result.Rating = Math.Round(average.GetDouble(), 1);
        }

        if (details.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            result.Genres = genres.EnumerateArray()
                .Select(g => StringOf(g, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }

        var minutes = Minutes(details);
        result.RunTimeTicks = minutes > 0 ? minutes * TicksPerMinute : null;

        var hasImages = details.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object;
        result.BackdropPath = (hasImages ? TextlessBackdrop(images) : null) ?? StringOf(details, "backdrop_path");
        result.LogoPath = hasImages ? Logo(images, language) : null;
        result.TrailerUrl = Trailer(details, language);
        return result;
    }

    // A movie has one runtime, a show lists what its episodes run.
    private static long Minutes(JsonElement details)
    {
        if (details.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number)
        {
            return runtime.GetInt64();
        }

        if (details.TryGetProperty("episode_run_time", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in episodes.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Number)
                {
                    return entry.GetInt64();
                }
            }
        }

        return 0;
    }

    private static string? TextlessBackdrop(JsonElement images)
    {
        if (!images.TryGetProperty("backdrops", out var backdrops) || backdrops.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return backdrops.EnumerateArray()
            .Where(b => LanguageOf(b) == null)
            .Select(b => StringOf(b, "file_path"))
            .FirstOrDefault(path => !string.IsNullOrEmpty(path));
    }

    // Most of the apps can't draw an SVG, so those logos are skipped.
    private static string? Logo(JsonElement images, string language)
    {
        if (!images.TryGetProperty("logos", out var logos) || logos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var usable = logos.EnumerateArray()
            .Select(l => (Language: LanguageOf(l), Path: StringOf(l, "file_path")))
            .Where(l => !string.IsNullOrEmpty(l.Path) && !l.Path!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var wanted in new[] { language, "en", null })
        {
            var match = usable.FirstOrDefault(l => l.Language == wanted);
            if (match.Path != null)
            {
                return match.Path;
            }
        }

        return null;
    }

    // A trailer beats a teaser, then an official upload, then one in the asked language.
    private static string? Trailer(JsonElement details, string language)
    {
        if (!details.TryGetProperty("videos", out var videos)
            || !videos.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var key = results.EnumerateArray()
            .Where(v => string.Equals(StringOf(v, "site"), "YouTube", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(StringOf(v, "key")))
            .Select(v => (Video: v, Type: StringOf(v, "type")))
            .Where(v => v.Type == "Trailer" || v.Type == "Teaser")
            .OrderBy(v => v.Type == "Trailer" ? 0 : 1)
            .ThenBy(v => v.Video.TryGetProperty("official", out var official) && official.ValueKind == JsonValueKind.True ? 0 : 1)
            .ThenBy(v => LanguageOf(v.Video) == language ? 0 : 1)
            .Select(v => StringOf(v.Video, "key"))
            .FirstOrDefault();
        return key == null ? null : "https://www.youtube.com/watch?v=" + key;
    }

    private static string? LanguageOf(JsonElement element) => StringOf(element, "iso_639_1");

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
