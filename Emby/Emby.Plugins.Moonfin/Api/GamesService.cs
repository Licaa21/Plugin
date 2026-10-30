using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using Moonfin.Games;
using Moonfin.Romm;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Emby-side retro-games API exposing the /Moonfin/Games and /Moonfin/EmulatorJS routes
    /// the Moonfin client uses.
    /// </summary>
    public class GamesService : IService, IRequiresRequest, IHasResultFactory
    {
        private const long MaxSaveBytes = 32 * 1024 * 1024;
        private const long MaxRommArchiveBytes = 512L * 1024 * 1024;
        private static readonly Assembly _assembly = typeof(GamesService).Assembly;

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public GamesService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }
        private object NotFound() { Request.Response.StatusCode = 404; return null!; }

        private static bool GamesEnabled() => Plugin.Instance?.Configuration?.GamesEnabled == true;

        private GamesScanner? Scanner()
        {
            var lm = PluginServices.LibraryManager;
            return lm == null ? null : new GamesScanner(lm, Romm());
        }

        // A user linked to RomM browses and downloads with their own RomM account.
        private RommSource? Romm() => Plugin.Instance?.Romm?.ForUser(AuthHelpers.GetCurrentUserId(Request, _authContext));

        public object Get(GetGamesDebugRequest request)
        {
            // Intentionally not gated on GamesEnabled: this diagnoses why libraries aren't detected.
            var scanner = Scanner();
            if (scanner == null) return Json(new { error = "Library manager unavailable" });
            return Json(scanner.GetDiagnostics());
        }

        public object Get(GetGameLibrariesRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetGameLibraries());
        }

        public object Get(GetGameSystemsRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetSystems(request.LibraryId));
        }

        public object Get(GetGamesRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetGames(request.LibraryId, request.System));
        }

        public object Get(GetGameDetailRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var game = scanner.GetGame(request.LibraryId, request.GameId);
            return game == null ? NotFound() : Json(game);
        }

        /// <summary>
        /// Streams a game's box art or screenshot, downloading and caching it on first ask. Takes a
        /// game token and a kind, never a URL, so this cannot be pointed at another host.
        /// </summary>
        public async Task<object> Get(GetGameThumbRequest request)
        {
            var scanner = Scanner();
            var thumbs = Plugin.Instance?.GameThumbs;
            if (!GamesEnabled() || scanner == null || thumbs == null) return NotFound();

            var source = scanner.ResolveThumbSource(request.LibraryId, request.GameId);
            if (source == null) return NotFound();

            var kind = GameThumbService.ParseKind(request.Type);
            var remoteUrl = kind == GameThumbService.ThumbKind.Boxart ? source.Value.BoxartUrl
                : kind == GameThumbService.ThumbKind.Snap ? source.Value.SnapUrl
                : null;
            var path = await thumbs
                .GetThumbPathAsync(source.Value.Core, source.Value.FileName, kind, remoteUrl)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();

            // Art for a given ROM name never changes, so let clients keep it. RomM art can be
            // replaced in RomM under the same game id, so that is only kept for an hour.
            Request.Response.AddHeader(
                "Cache-Control",
                Romm()?.Owns(request.LibraryId) == true ? "public,max-age=3600" : "public,max-age=31536000,immutable");
            Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
            return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
        }

        public async Task<object> Get(GetGameRomRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var romm = Romm();
            if (romm?.Owns(request.LibraryId) == true) return await ServeRommRomAsync(romm, request.Token).ConfigureAwait(false);
            var path = scanner.ResolveFilePath(request.LibraryId, request.Token, allowBios: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            if (GamesScanner.IsCueSheet(path))
            {
                var discSet = DiscSet.ForCueSheet(path);
                return discSet == null ? NotFound() : new DiscSetZipResult(discSet);
            }
            if (GamesScanner.IsArchive(path))
            {
                var core = scanner.SystemCoreFor(request.LibraryId, path) ?? string.Empty;
                if (GamesScanner.ShouldPreserveArchiveForCore(core, path))
                    return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
                if (GamesScanner.ShouldRepackArchiveForCore(core, path))
                {
                    var discSet = DiscSet.ForSevenZip(path);
                    return discSet == null ? NotFound() : new DiscSetZipResult(discSet);
                }

                byte[]? rom;
                try { rom = GamesScanner.ExtractRomFromArchive(path); }
                catch { return NotFound(); }
                if (rom == null || rom.Length == 0) return NotFound();
                // Unpack the archive in memory so the client gets raw ROM bytes. The file on disk is untouched.
                return ResultFactory.GetResult(Request, new MemoryStream(rom), "application/octet-stream", null);
            }
            return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
        }

        public async Task<object> Get(GetGameBiosRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var romm = Romm();
            if (romm?.Owns(request.LibraryId) == true)
            {
                var firmware = romm.FindFirmware(request.Token);
                var client = romm.CreateClient();
                if (firmware == null || client == null) return NotFound();
                return await ProxyRommAsync(range => client.SendFirmwareAsync(HttpMethod.Get, firmware, range, Request.CancellationToken)).ConfigureAwait(false);
            }

            var path = scanner.ResolveFilePath(request.LibraryId, request.Token, allowBios: true);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
        }

        public object Head(HeadGameRomRequest request)
        {
            var romm = Romm();
            if (GamesEnabled() && romm?.Owns(request.LibraryId) == true)
            {
                // RomM listed every size, unpacked archives included, so no request goes upstream.
                var game = romm.FindGame(request.Token);
                return game == null ? NotFound() : HeadResult(game.ServedSizeBytes);
            }

            return SizeOnly(request.LibraryId, request.Token, allowBios: false);
        }

        public object Head(HeadGameBiosRequest request)
        {
            var romm = Romm();
            if (GamesEnabled() && romm?.Owns(request.LibraryId) == true)
            {
                var firmware = romm.FindFirmware(request.Token);
                return firmware == null ? NotFound() : HeadResult(firmware.SizeBytes);
            }

            return SizeOnly(request.LibraryId, request.Token, allowBios: true);
        }

        // Answers with the length the matching GET would send and no body. An archived ROM is
        // measured from the archive index, since the GET serves the unpacked entry.
        private object SizeOnly(string libraryId, string token, bool allowBios)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var path = scanner.ResolveFilePath(libraryId, token, allowBios);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();

            long? length = new FileInfo(path).Length;
            if (!allowBios && GamesScanner.IsCueSheet(path))
                length = DiscSet.ForCueSheet(path)?.Length;
            else if (!allowBios && GamesScanner.IsArchive(path))
            {
                var core = scanner.SystemCoreFor(libraryId, path) ?? string.Empty;
                if (GamesScanner.ShouldRepackArchiveForCore(core, path))
                    length = DiscSet.ForSevenZip(path)?.Length;
                else if (!GamesScanner.ShouldPreserveArchiveForCore(core, path))
                    length = GamesScanner.GetExtractedRomInfo(path)?.Length;
            }
            if (length == null) return NotFound();

            return HeadResult(length.Value);
        }

        private object HeadResult(long length)
        {
            return ResultFactory.GetStaticResult(Request, new StaticResultOptions
            {
                ContentType = "application/octet-stream",
                ContentLength = length,
                IsHeadRequest = true,
                SupportsRangeRequests = true,
                // A head request has no body to produce, but a factory is supplied so nothing
                // can trip over a missing one.
                ContentFactory = (start, end, cancel) => Task.FromResult(new StreamHandler
                {
                    Stream = Stream.Null,
                    Length = 0,
                    TotalLength = length,
                }),
            });
        }

        public async Task<object> Get(GetRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var accounts = Plugin.Instance?.Romm?.Accounts;
            return Json(accounts == null || !GamesEnabled()
                ? new RommAccountStatus()
                : await accounts.StatusAsync(userId.Value, Request.CancellationToken).ConfigureAwait(false));
        }

        public async Task<object> Post(LinkRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var accounts = Plugin.Instance?.Romm?.Accounts;
            return Json(accounts == null || !GamesEnabled()
                ? new RommAccountStatus()
                : await accounts.StartLinkAsync(userId.Value, Request.CancellationToken).ConfigureAwait(false));
        }

        public object Delete(UnlinkRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            return Json(Plugin.Instance?.Romm?.Accounts?.Unlink(userId.Value) ?? new RommAccountStatus());
        }

        public async Task<object> Post(TestRommRequest request)
        {
            var romm = Romm();
            var baseUrl = RommClient.NormalizeBaseUrl(request.Url);
            var token = RommClient.NormalizeToken(request.Token);
            if (romm == null) return Json(new RommTestResult { Error = "The plugin is still starting." });
            if (baseUrl == null) return Json(new RommTestResult { Error = "Enter the RomM address, like http://192.168.1.20:8080." });
            if (token == null) return Json(new RommTestResult { Error = "Enter a RomM client token." });

            try
            {
                var info = await romm.ClientFor(baseUrl, token).CheckAsync(Request.CancellationToken).ConfigureAwait(false);
                return Json(new RommTestResult
                {
                    Ok = true,
                    Version = info.Version,
                    PlatformCount = info.PlatformCount,
                    SupportedPlatformCount = info.SupportedPlatformCount,
                    SupportedRomCount = info.SupportedRomCount,
                });
            }
            catch (Exception ex) when (ex is JsonException || ex is RommRequestException { StatusCode: 404 })
            {
                return Json(new RommTestResult { Error = "That address answered, but not like a RomM server." });
            }
            catch (RommRequestException ex)
            {
                return Json(new RommTestResult { Error = ex.Message + "." });
            }
            catch (HttpRequestException)
            {
                return Json(new RommTestResult { Error = "Couldn't reach RomM at that address." });
            }
            catch (TaskCanceledException)
            {
                return Json(new RommTestResult { Error = "RomM didn't answer in time." });
            }
        }

        private async Task<object> ServeRommRomAsync(RommSource romm, string gameId)
        {
            var game = romm.FindGame(gameId);
            var client = romm.CreateClient();
            if (game == null || client == null) return NotFound();

            if (!game.UnpackArchive)
            {
                return await ProxyRommAsync(range => client.SendRomAsync(HttpMethod.Get, game, range, Request.CancellationToken)).ConfigureAwait(false);
            }

            // A zipped ROM is unpacked here like one in a folder library, so the app gets the same
            // raw bytes either way.
            var temp = Path.Combine(Path.GetTempPath(), $"moonfin-romm-{Guid.NewGuid():N}{Path.GetExtension(game.Rom.FileName)}");
            try
            {
                var status = await client.DownloadRomAsync(game, temp, MaxRommArchiveBytes, Request.CancellationToken).ConfigureAwait(false);
                if (status == HttpStatusCode.RequestEntityTooLarge)
                {
                    return Json(413, new { error = "The archive exceeds the " + (MaxRommArchiveBytes / (1024 * 1024)) + " MB limit for extracted ROMs." });
                }

                if (status != HttpStatusCode.OK) return RommFailure(status);

                byte[]? rom;
                try { rom = GamesScanner.ExtractRomFromArchive(temp); }
                catch { return NotFound(); }
                if (rom == null || rom.Length == 0) return NotFound();
                return ResultFactory.GetResult(Request, new MemoryStream(rom), "application/octet-stream", null);
            }
            catch (HttpRequestException)
            {
                Request.Response.StatusCode = 502;
                return null!;
            }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException)
                {
                    // A temp file left behind is cleared with the rest of the temp folder.
                }
            }
        }

        /// <summary>
        /// Streams a RomM file to the app. A byte range is passed through so a client can resume or
        /// probe the size the way it does against a file on disk.
        /// </summary>
        private async Task<object> ProxyRommAsync(Func<string?, Task<HttpResponseMessage>> send)
        {
            HttpResponseMessage upstream;
            try
            {
                upstream = await send(Request.Headers?.Get("Range")).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                Request.Response.StatusCode = 502;
                return null!;
            }
            catch (TaskCanceledException)
            {
                Request.Response.StatusCode = 504;
                return null!;
            }

            if (upstream.StatusCode != HttpStatusCode.OK && upstream.StatusCode != HttpStatusCode.PartialContent)
            {
                using (upstream) return RommFailure(upstream.StatusCode);
            }

            var headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" };
            if (upstream.Content.Headers.ContentLength is long length)
            {
                headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
            }

            if (upstream.Content.Headers.ContentRange != null)
            {
                headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
            }

            Request.Response.StatusCode = (int)upstream.StatusCode;
            // Emby disposes the stream once it is written, which releases the RomM connection.
            var stream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return ResultFactory.GetResult(Request, stream, "application/octet-stream", headers);
        }

        private object RommFailure(HttpStatusCode status)
        {
            Request.Response.StatusCode = status == HttpStatusCode.NotFound ? 404
                : status == HttpStatusCode.RequestedRangeNotSatisfiable ? 416
                : 502;
            return null!;
        }

        public async Task<object> Get(GetGameSaveRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var rommSaves = Plugin.Instance?.RommSaves;
            var target = RommTarget(rommSaves, userId.Value, request.GameId, request.Kind);
            var data = target != null
                ? await rommSaves!.ReadAsync(target, Request.CancellationToken).ConfigureAwait(false)
                : GameSavesHelper.Get(userId.Value, request.GameId, request.Kind ?? "state");
            if (data == null) return NotFound();
            return ResultFactory.GetResult(Request, new MemoryStream(data), "application/octet-stream", null);
        }

        public async Task<object> Put(PutGameSaveRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            using var ms = new MemoryStream();
            if (request.RequestStream != null) await request.RequestStream.CopyToAsync(ms).ConfigureAwait(false);
            if (ms.Length == 0) return Json(400, new { error = "Empty save payload." });
            if (ms.Length > MaxSaveBytes) return Json(400, new { error = "Save payload too large." });

            var rommSaves = Plugin.Instance?.RommSaves;
            var target = RommTarget(rommSaves, userId.Value, request.GameId, request.Kind);
            if (target != null)
            {
                await rommSaves!.WriteAsync(target, ms.ToArray(), Request.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                GameSavesHelper.Save(userId.Value, request.GameId, request.Kind ?? "state", ms.ToArray());
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        private static RommSaveTarget? RommTarget(RommSaveSync? rommSaves, Guid userId, string gameId, string? kind) =>
            rommSaves?.Target(userId, gameId, kind, GameSavesHelper.ResolvePath(userId, gameId, kind ?? "state"));

        public object Get(GetCoresStatusRequest request)
        {
            return Json(GameCoresHelper.GetStatus());
        }

        public object Post(InstallCoresRequest request)
        {
            GameCoresHelper.StartInstall();
            return Json(202, GameCoresHelper.GetStatus());
        }

        public async Task<object> Post(UploadCoresRequest request)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"emulatorjs-upload-{Guid.NewGuid():N}.zip");
            using (var dst = File.Create(tempFile))
            {
                if (request.RequestStream != null) await request.RequestStream.CopyToAsync(dst).ConfigureAwait(false);
            }

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
                return Json(400, new { error = "Empty upload." });
            }

            GameCoresHelper.StartInstallFromFile(tempFile);
            return Json(202, GameCoresHelper.GetStatus());
        }

        public object Get(GetEmulatorPlayerRequest request)
        {
            var stream = _assembly.GetManifestResourceStream("Emby.Plugins.Moonfin.EmulatorJS.player.html");
            if (stream == null) return NotFound();
            string html;
            using (var reader = new StreamReader(stream)) html = reader.ReadToEnd();
            html = html.Replace("__EJS_PATHTODATA__", GameCoresHelper.ResolveDataPath());

            // Cores that need SharedArrayBuffer (e.g. PSP) only work in a cross-origin isolated
            // document. Send the isolation headers just for those so cartridge and single-threaded
            // disc cores keep loading normally.
            System.Collections.Generic.IDictionary<string, string>? headers = null;
            if (string.Equals(Request?.QueryString?.Get("core"), "psp", StringComparison.Ordinal))
            {
                headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["Cross-Origin-Opener-Policy"] = "same-origin",
                    ["Cross-Origin-Embedder-Policy"] = "credentialless"
                };
            }

            return ResultFactory.GetResult(Request, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), "text/html; charset=utf-8", headers);
        }

        public object Get(GetEmulatorDataRequest request)
        {
            var dataRoot = GameCoresHelper.LocalDataRoot();
            if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(dataRoot)) return NotFound();

            var requested = string.IsNullOrWhiteSpace(request.Path) ? "loader.js" : request.Path!;
            if (!TryResolveContainedPath(dataRoot, requested, out var fullPath) || !File.Exists(fullPath)) return NotFound();
            return ResultFactory.GetStaticFileResult(Request, fullPath);
        }

        private static bool TryResolveContainedPath(string rootPath, string requestPath, out string fullPath)
        {
            var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
            var candidate = Path.Combine(rootPath, normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
            fullPath = Path.GetFullPath(candidate);
            var normalizedRoot = Path.GetFullPath(rootPath);
            var rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) || string.Equals(fullPath, normalizedRoot, StringComparison.Ordinal);
        }
    }
}
