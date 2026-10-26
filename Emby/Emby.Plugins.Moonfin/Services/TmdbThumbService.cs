using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Api;
using MediaBrowser.Controller.Configuration;
using Moonfin.Tmdb;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Reads what a client shows for a movie or show the server doesn't have. The wide image
    /// prefers a backdrop in the metadata language, since those carry the title the way a library
    /// thumb does, and falls back to the main backdrop. The details come from the same call.
    /// </summary>
    public class TmdbThumbService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        private readonly IServerConfigurationManager _configManager;
        private readonly TmdbRatingService _ratings;
        private readonly HttpClient _client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");
        private readonly ConcurrentDictionary<string, (Entry? Value, DateTimeOffset CachedAt)> _cache =
            new ConcurrentDictionary<string, (Entry?, DateTimeOffset)>();

        public TmdbThumbService(IServerConfigurationManager configManager, TmdbRatingService ratings)
        {
            _configManager = configManager;
            _ratings = ratings;
        }

        /// <summary>
        /// The TMDB image path for the title, or null when TMDB has no backdrop for it. An IMDb id
        /// is only used when there is no TMDB id.
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
                var found = await _ratings.FindByImdbAsync(imdbId!, apiKey, cancellationToken).ConfigureAwait(false);
                if (found == null) return null;

                mediaType = found.Value.MediaType;
                tmdbId = found.Value.TmdbId;
            }

            if (tmdbId == null) return null;

            var key = mediaType + ":" + tmdbId;
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheTtl)
                return cached.Value;

            var language = MetadataLanguage(_configManager.Configuration.PreferredMetadataLanguage);
            var url = "https://api.themoviedb.org/3/" + mediaType + "/" + tmdbId + "?" + TmdbTitleDetails.Query(language);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            MoonfinHttp.ApplyTmdbAuth(request, apiKey);

            try
            {
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _cache[key] = (null, DateTimeOffset.UtcNow);
                    return null;
                }

                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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
            if (titled != null) return titled;

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
                return null;

            var titled = backdrops.EnumerateArray().FirstOrDefault(b =>
                b.TryGetProperty("iso_639_1", out var lang) && string.Equals(lang.GetString(), language, StringComparison.OrdinalIgnoreCase));
            return titled.ValueKind == JsonValueKind.Object && titled.TryGetProperty("file_path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }

        private sealed class Entry
        {
            public Entry(string? thumbPath, TmdbTitleDetails details)
            {
                ThumbPath = thumbPath;
                Details = details;
            }

            public string? ThumbPath { get; }

            public TmdbTitleDetails Details { get; }
        }
    }
}
