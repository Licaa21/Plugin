using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using Moonfin.Tmdb;

namespace Moonfin.Server.Services;

/// <summary>
/// Reads what a client shows for a movie or show the server doesn't have. The wide image prefers
/// a backdrop in the metadata language, since those carry the title the way a library thumb
/// does, and falls back to the main backdrop. The details come from the same call.
/// </summary>
public class TmdbThumbService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _configManager;
    private readonly TmdbRatingService _ratings;
    private readonly ILogger<TmdbThumbService> _logger;
    private readonly ConcurrentDictionary<string, (Entry? Value, DateTimeOffset CachedAt)> _cache = new();

    public TmdbThumbService(
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager configManager,
        TmdbRatingService ratings,
        ILogger<TmdbThumbService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _ratings = ratings;
        _logger = logger;
    }

    /// <summary>
    /// Gives items from outside lists the overview, genres and runtime TMDB holds for them, so a
    /// focused card can describe the title the way it does a library one. Only items without an
    /// overview are looked up, and nothing happens without a key.
    /// </summary>
    public async Task FillDetailsAsync(IReadOnlyList<CustomRowItem> items, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var pending = items.Where(i => string.IsNullOrEmpty(i.Overview)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(TmdbRatingService.Parallelism);
        await Task.WhenAll(pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long? tmdbId = long.TryParse(item.ProviderIds?.Tmdb, out var parsed) ? parsed : null;
                var details = await GetDetailsAsync(TmdbRatingService.MediaTypeOf(item), tmdbId, item.ProviderIds?.Imdb, apiKey, cancellationToken).ConfigureAwait(false);
                if (details != null)
                {
                    ApplyDetails(item, details);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not look up details for {Name}", item.Name);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    /// <summary>Fills in whatever the item is missing from what TMDB holds for it.</summary>
    internal static void ApplyDetails(CustomRowItem item, TmdbTitleDetails details)
    {
        if (string.IsNullOrEmpty(item.Overview))
        {
            item.Overview = details.Overview;
        }

        if ((item.Genres == null || item.Genres.Count == 0) && details.Genres.Count > 0)
        {
            item.Genres = details.Genres.ToList();
        }

        item.RunTimeTicks ??= details.RunTimeTicks;
    }

    /// <summary>
    /// The TMDB image path for the title, or null when TMDB has no backdrop for it. An IMDb id is
    /// only used when there is no TMDB id.
    /// </summary>
    public async Task<string?> GetPathAsync(string mediaType, long? tmdbId, string? imdbId, string apiKey, CancellationToken cancellationToken)
    {
        return (await GetEntryAsync(mediaType, tmdbId, imdbId, apiKey, cancellationToken).ConfigureAwait(false))?.ThumbPath;
    }

    /// <summary>
    /// The overview, score, logo and trailer TMDB holds for the title in the server's metadata
    /// language, or null when TMDB doesn't know it.
    /// </summary>
    public async Task<TmdbTitleDetails?> GetDetailsAsync(string mediaType, long? tmdbId, string? imdbId, string apiKey, CancellationToken cancellationToken)
    {
        return (await GetEntryAsync(mediaType, tmdbId, imdbId, apiKey, cancellationToken).ConfigureAwait(false))?.Details;
    }

    private async Task<Entry?> GetEntryAsync(string mediaType, long? tmdbId, string? imdbId, string apiKey, CancellationToken cancellationToken)
    {
        if (tmdbId == null && !string.IsNullOrWhiteSpace(imdbId))
        {
            var found = await _ratings.FindByImdbAsync(imdbId, apiKey, cancellationToken).ConfigureAwait(false);
            if (found == null)
            {
                return null;
            }

            mediaType = found.Value.MediaType;
            tmdbId = found.Value.TmdbId;
        }

        if (tmdbId == null)
        {
            return null;
        }

        var key = $"{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheTtl)
        {
            return cached.Value;
        }

        var language = MetadataLanguage(_configManager.Configuration.PreferredMetadataLanguage);
        var url = $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}?{TmdbTitleDetails.Query(language)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TmdbRequestHelper.ApplyAuth(request, apiKey);

        try
        {
            var client = _httpClientFactory.CreateClient("MoonfinHttpClient");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache[key] = (null, DateTimeOffset.UtcNow);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var entry = new Entry(PickPath(doc.RootElement, language), TmdbTitleDetails.Parse(doc.RootElement, language));
            _cache[key] = (entry, DateTimeOffset.UtcNow);
            return entry;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static string MetadataLanguage(string? configured)
    {
        var code = (configured ?? string.Empty).Split('-')[0].Trim().ToLowerInvariant();
        return code.Length == 2 ? code : "en";
    }

    internal static string? PickPath(JsonElement details, string language)
    {
        var titled = TitledBackdrop(details, language);
        if (titled != null)
        {
            return titled;
        }

        return details.TryGetProperty("backdrop_path", out var backdrop) && backdrop.ValueKind == JsonValueKind.String
            ? backdrop.GetString()
            : null;
    }

    /// <summary>
    /// The backdrop in <paramref name="language"/> from details fetched with their images, or
    /// null when TMDB has none in that language.
    /// </summary>
    private static string? TitledBackdrop(JsonElement details, string language)
    {
        if (!details.TryGetProperty("images", out var images) || !images.TryGetProperty("backdrops", out var backdrops) || backdrops.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var titled = backdrops.EnumerateArray().FirstOrDefault(b =>
            b.TryGetProperty("iso_639_1", out var lang) && string.Equals(lang.GetString(), language, StringComparison.OrdinalIgnoreCase));
        return titled.ValueKind == JsonValueKind.Object && titled.TryGetProperty("file_path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;
    }

    private sealed record Entry(string? ThumbPath, TmdbTitleDetails Details);
}
