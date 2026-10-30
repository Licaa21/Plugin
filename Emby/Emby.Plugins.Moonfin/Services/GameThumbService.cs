using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Api;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches libretro box art and screenshots and caches them under game_thumbs/.
    ///
    /// Clients cannot reach thumbnails.libretro.com directly from a browser because it serves no
    /// CORS headers, so the art is fetched here and served back from the plugin's own origin.
    /// Going through the server also means one download is shared by every client instead of each
    /// of them hitting libretro on every scroll.
    /// </summary>
    public class GameThumbService
    {
        /// <summary>The libretro thumbnail folder for a kind of art.</summary>
        public enum ThumbKind
        {
            Boxart,
            Snap,
            Title
        }

        // EmulatorJS core -> libretro platform name, which is also the thumbnail folder.
        private static readonly Dictionary<string, string> CoreToPlatform =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nes"] = "Nintendo - Nintendo Entertainment System",
                ["snes"] = "Nintendo - Super Nintendo Entertainment System",
                ["gb"] = "Nintendo - Game Boy",
                // Not a core of its own, see GamesScanner.ArtworkCore.
                ["gbc"] = "Nintendo - Game Boy Color",
                ["gba"] = "Nintendo - Game Boy Advance",
                ["n64"] = "Nintendo - Nintendo 64",
                ["nds"] = "Nintendo - Nintendo DS",
                ["vb"] = "Nintendo - Virtual Boy",
                ["segaMD"] = "Sega - Mega Drive - Genesis",
                ["segaMS"] = "Sega - Master System - Mark III",
                ["segaGG"] = "Sega - Game Gear",
                ["atari2600"] = "Atari - 2600",
                ["atari7800"] = "Atari - 7800",
                ["lynx"] = "Atari - Lynx",
                ["ws"] = "Bandai - WonderSwan",
                ["ngp"] = "SNK - Neo Geo Pocket",
                ["pce"] = "NEC - PC Engine - TurboGrafx 16",
                ["psx"] = "Sony - PlayStation",
                ["psp"] = "Sony - PlayStation Portable",
            };

        // Game Boy and Game Boy Color carts often share a folder, and their art is split between
        // the two sets, so a miss in one tries the other.
        private static readonly Dictionary<string, string> SiblingCore =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gb"] = "gbc",
                ["gbc"] = "gb",
            };

        private readonly ILogger _logger;
        private readonly string _cacheDir;

        // Names that resolved to a 404 upstream. Thumbnails are keyed on the No-Intro name, so a
        // ROM whose filename does not match has no art at all and re-asking on every request would
        // just be a slow way to fail.
        private readonly ConcurrentDictionary<string, byte> _misses = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // Downloads already running, keyed the same way as the cache.
        private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new ConcurrentDictionary<string, Lazy<Task<string?>>>(StringComparer.Ordinal);

        public GameThumbService(ILogger logger)
        {
            _logger = logger;

            var dataPath = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");
            _cacheDir = Path.Combine(dataPath, "game_thumbs");
        }

        public static ThumbKind ParseKind(string? kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case "snap": return ThumbKind.Snap;
                case "title": return ThumbKind.Title;
                default: return ThumbKind.Boxart;
            }
        }

        /// <summary>
        /// The cached file for a game's art, downloading it first when needed. A RomM game passes
        /// its own art's URL, tried before the libretro name. Null when nothing has art for it or
        /// every download failed.
        /// </summary>
        public async Task<string?> GetThumbPathAsync(string core, string romFileName, ThumbKind kind, string? remoteUrl = null)
        {
            if (!string.IsNullOrEmpty(remoteUrl))
            {
                var remote = await GetCachedAsync(remoteUrl!, "url-" + Sha1Hex(remoteUrl!), kind, remoteUrl!).ConfigureAwait(false);
                if (remote != null)
                {
                    return remote;
                }
            }

            var name = LibretroThumbName(romFileName);
            if (name.Length == 0)
            {
                return null;
            }

            foreach (var platform in PlatformsFor(core))
            {
                var path = await GetCachedAsync(BuildUrl(platform, kind, name), CacheKey(platform, kind, name), kind, name).ConfigureAwait(false);
                if (path != null)
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>The libretro platforms tried for a core's art, in order.</summary>
        internal static List<string> PlatformsFor(string core)
        {
            var platforms = new List<string>();
            if (string.IsNullOrEmpty(core) || !CoreToPlatform.TryGetValue(core, out var platform))
            {
                return platforms;
            }

            platforms.Add(platform);
            if (SiblingCore.TryGetValue(core, out var sibling))
            {
                platforms.Add(CoreToPlatform[sibling]);
            }

            return platforms;
        }

        private async Task<string?> GetCachedAsync(string url, string cacheKey, ThumbKind kind, string name)
        {
            if (_misses.ContainsKey(cacheKey))
            {
                return null;
            }

            var localPath = Path.Combine(_cacheDir, cacheKey + ".png");
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // The detail screen asks for the same box art for its poster and its blurred backdrop at
            // once. GetOrAdd can run its factory on several threads but only ever returns one Lazy, so
            // holding the Lazy rather than the task is what keeps that to a single download.
            var lazy = _inFlight.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<string?>>(() => DownloadAsync(url, kind, name, cacheKey, localPath)));
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                Lazy<Task<string?>> done;
                _inFlight.TryRemove(cacheKey, out done);
            }
        }

        private async Task<string?> DownloadAsync(string url, ThumbKind kind, string name, string cacheKey, string localPath)
        {
            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(30), "Moonfin/1.0");
                using var response = await client.GetAsync(url).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _misses.TryAdd(cacheKey, 0);
                    return null;
                }

                response.EnsureSuccessStatusCode();
                var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                Directory.CreateDirectory(_cacheDir);
                // Write beside the target and rename, so a failed download cannot leave a torn file
                // behind for every later request to serve.
                var temp = localPath + ".tmp";
                File.WriteAllBytes(temp, data);
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }

                File.Move(temp, localPath);
                return localPath;
            }
            catch (Exception ex)
            {
                _logger.Debug("Downloading {0} art for {1} failed: {2}", kind, name, ex.Message);
                return null;
            }
        }

        private static string BuildUrl(string platform, ThumbKind kind, string name)
        {
            return "https://thumbnails.libretro.com/"
                + Uri.EscapeDataString(platform) + "/" + FolderFor(kind) + "/"
                + Uri.EscapeDataString(name) + ".png";
        }

        private static string FolderFor(ThumbKind kind)
        {
            switch (kind)
            {
                case ThumbKind.Snap: return "Named_Snaps";
                case ThumbKind.Title: return "Named_Titles";
                default: return "Named_Boxarts";
            }
        }

        // The characters libretro rewrites to '_' in thumbnail filenames.
        private static readonly char[] ReservedChars = { '&', '*', '/', ':', '`', '<', '>', '?', '\\', '|', '"' };

        /// <summary>
        /// The name libretro keys thumbnails on: the ROM filename with its region and revision tags,
        /// minus the extension. This is the filename rather than the display title, which for a game
        /// in its own folder is the folder name and would not match.
        /// </summary>
        private static string LibretroThumbName(string romFileName)
        {
            var chars = (Path.GetFileNameWithoutExtension(romFileName) ?? string.Empty).ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(ReservedChars, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        // Hashed down to one flat file name so the cache stays a single directory and no ROM name can
        // steer the path. Platform and kind are in the key because the same name exists on several
        // systems.
        private static string CacheKey(string platform, ThumbKind kind, string name)
        {
            return Sha1Hex(platform + "|" + FolderFor(kind) + "|" + name);
        }

        private static string Sha1Hex(string raw)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
