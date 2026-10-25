using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonfin.Tmdb;

namespace Moonfin.Server.Services;

/// <summary>What a proxy route answers: an HTTP status and a JSON body.</summary>
public sealed record TmdbProxyResult(int Status, string Json);

/// <summary>
/// Answers the Smart TV app's TMDB text, season and recommendation requests with the server-wide
/// key, so the app needs no key of its own. Bodies keep TMDB's own snake_case field names. The key
/// stays in the request to TMDB and is never written into an answer, an error or a log line.
/// </summary>
public sealed class TmdbProxyService
{
    // ponytail: counts entries, not bytes. A Season answer with every crew list can run to a few
    // hundred KB, so the worst case is tens of MB per route. Add a size budget if that ever matters.
    private const int DefaultMaxEntries = 500;
    private const int MaxPage = 500;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly ILogger _logger;
    private readonly TtlCache _details;
    private readonly TtlCache _seasons;
    private readonly TtlCache _recommendations;

    public TmdbProxyService(ILogger<TmdbProxyService> logger)
        : this(logger, DefaultMaxEntries, () => DateTimeOffset.UtcNow)
    {
    }

    internal TmdbProxyService(ILogger logger, int maxEntries, Func<DateTimeOffset> now)
    {
        _logger = logger;
        _details = new TtlCache(maxEntries, now);
        _seasons = new TtlCache(maxEntries, now);
        _recommendations = new TtlCache(maxEntries, now);
    }

    public Task<TmdbProxyResult> DetailsAsync(
        HttpClient client, string? apiKey, string? mediaType, string? tmdbId, string? language, CancellationToken ct)
    {
        if (!TryMediaType(mediaType, out var type) || !TryId(tmdbId, out var id))
        {
            return Task.FromResult(Bad("mediaType must be movie or tv, and tmdbId a TMDB id"));
        }

        var lang = TmdbReview.RequestLanguage(language);
        return Serve("Details", _details, $"{type}:{id}:{lang}", apiKey,
            $"{type}/{id}?language={Uri.EscapeDataString(lang)}", client, ct,
            root => root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number,
            (w, root) =>
            {
                Copy(w, root, "id", "title", "name", "original_title", "overview", "tagline");
                if (type != "tv")
                {
                    return;
                }

                w.WriteStartArray("seasons");
                if (root.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var season in seasons.EnumerateArray())
                    {
                        w.WriteStartObject();
                        Copy(w, season, "season_number", "name");
                        w.WriteEndObject();
                    }
                }

                w.WriteEndArray();
                if (root.TryGetProperty("next_episode_to_air", out var next) && next.ValueKind == JsonValueKind.Object)
                {
                    w.WriteStartObject("next_episode_to_air");
                    Copy(w, next, "season_number", "episode_number", "air_date", "name");
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteNull("next_episode_to_air");
                }
            });
    }

    public Task<TmdbProxyResult> SeasonAsync(
        HttpClient client, string? apiKey, string? tmdbId, string? seasonNumber, string? language, CancellationToken ct)
    {
        if (!TryId(tmdbId, out var id) || !TryWhole(seasonNumber, out var season))
        {
            return Task.FromResult(Bad("tmdbId must be a TMDB id and seasonNumber a season number"));
        }

        var lang = TmdbReview.RequestLanguage(language);
        return Serve("Season", _seasons, $"{id}:{season}:{lang}", apiKey,
            $"tv/{id}/season/{season}?language={Uri.EscapeDataString(lang)}", client, ct,
            root => root.TryGetProperty("episodes", out var e) && e.ValueKind == JsonValueKind.Array,
            (w, root) =>
            {
                Copy(w, root, "season_number", "name", "overview");
                w.WriteStartArray("episodes");
                foreach (var episode in root.GetProperty("episodes").EnumerateArray())
                {
                    w.WriteStartObject();
                    Copy(w, episode, "season_number", "episode_number", "name", "overview", "air_date", "runtime",
                        "still_path", "vote_average", "vote_count", "crew", "guest_stars");
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            });
    }

    public Task<TmdbProxyResult> RecommendationsAsync(
        HttpClient client, string? apiKey, string? mediaType, string? tmdbId, string? language, string? page, CancellationToken ct)
    {
        if (!TryMediaType(mediaType, out var type) || !TryId(tmdbId, out var id) || !TryWhole(string.IsNullOrWhiteSpace(page) ? "1" : page, out var pageNumber) || pageNumber is < 1 or > MaxPage)
        {
            return Task.FromResult(Bad($"mediaType must be movie or tv, tmdbId a TMDB id and page 1 to {MaxPage}"));
        }

        var lang = TmdbReview.RequestLanguage(language);
        return Serve("Recommendations", _recommendations, $"{type}:{id}:{lang}:{pageNumber}", apiKey,
            $"{type}/{id}/recommendations?language={Uri.EscapeDataString(lang)}&page={pageNumber}", client, ct,
            root => root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array,
            (w, root) => Copy(w, root, "page", "total_pages", "results"));
    }

    private async Task<TmdbProxyResult> Serve(
        string route, TtlCache cache, string cacheKey, string? apiKey, string path, HttpClient client,
        CancellationToken ct, Func<JsonElement, bool> looksLikeTmdb, Action<Utf8JsonWriter, JsonElement> shape)
    {
        // A key pasted with a trailing space or newline would otherwise reach TMDB as a 401 and look like an outage.
        apiKey = apiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            return Fail(503, "TMDB is not set up on this server");
        }

        if (cache.TryGet(cacheKey, out var cached))
        {
            return new TmdbProxyResult(200, cached);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/{path}");
            TmdbRequestHelper.ApplyAuth(request, apiKey);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail(404, "TMDB does not know this id");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB answered {Route} with status {Status}", route, (int)response.StatusCode);
                return Fail(502, "TMDB did not answer");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !looksLikeTmdb(doc.RootElement))
            {
                _logger.LogWarning("TMDB answered {Route} with something that is not TMDB's", route);
                return Fail(502, "TMDB answered something unexpected");
            }

            using var buffer = new MemoryStream();
            // Relaxed so non-English text stays as it is rather than \uXXXX, which would triple its size.
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                shape(writer, doc.RootElement);
                writer.WriteEndObject();
            }

            var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            cache.Set(cacheKey, json);
            return new TmdbProxyResult(200, json);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            // The type only: a message could carry the request address, and a v3 key rides in it.
            _logger.LogWarning("TMDB {Route} failed: {Error}", route, ex.GetType().Name);
            return Fail(502, "TMDB did not answer");
        }
    }

    private static void Copy(Utf8JsonWriter writer, JsonElement from, params string[] names)
    {
        foreach (var name in names)
        {
            if (from.TryGetProperty(name, out var value))
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
        }
    }

    private static bool TryMediaType(string? asked, out string type)
    {
        type = asked?.Trim().ToLowerInvariant() ?? string.Empty;
        return type is "movie" or "tv";
    }

    private static bool TryWhole(string? asked, out int number) =>
        int.TryParse(asked?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);

    private static bool TryId(string? asked, out long id) =>
        long.TryParse(asked?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;

    private static TmdbProxyResult Bad(string message) => Fail(400, message);

    private static TmdbProxyResult Fail(int status, string message) =>
        new(status, JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = message }));

    /// <summary>
    /// Answers kept for a day, at most <c>max</c> of them: when full, expired ones go first and then
    /// the oldest.
    /// </summary>
    private sealed class TtlCache
    {
        private readonly Dictionary<string, (string Json, DateTimeOffset At)> _items = new();
        private readonly int _max;
        private readonly Func<DateTimeOffset> _now;

        public TtlCache(int max, Func<DateTimeOffset> now)
        {
            _max = max;
            _now = now;
        }

        public bool TryGet(string key, out string json)
        {
            lock (_items)
            {
                if (_items.TryGetValue(key, out var hit))
                {
                    if (_now() - hit.At < CacheTtl)
                    {
                        json = hit.Json;
                        return true;
                    }

                    _items.Remove(key);
                }
            }

            json = string.Empty;
            return false;
        }

        public void Set(string key, string json)
        {
            lock (_items)
            {
                if (_items.Count >= _max && !_items.ContainsKey(key))
                {
                    var now = _now();
                    foreach (var stale in _items.Where(i => now - i.Value.At >= CacheTtl).Select(i => i.Key).ToList())
                    {
                        _items.Remove(stale);
                    }

                    if (_items.Count >= _max)
                    {
                        _items.Remove(_items.MinBy(i => i.Value.At).Key);
                    }
                }

                _items[key] = (json, _now());
            }
        }
    }
}
