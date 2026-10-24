using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The routes the Smart TV app calls instead of asking TMDB itself. TMDB is faked with a handler,
/// so these check what the app would get back and that the key never leaves the server.
/// </summary>
public class TmdbProxyServiceTests
{
    // Not a real key; the tests only need something recognisable to look for in the answers.
    private const string Key = "fake-test-key-0123456789";

    private const string TvDetails = """
        {"id":1399,"name":"Game of Thrones","original_name":"Game of Thrones","overview":"Nine noble families.","tagline":"Winter is coming.",
         "genres":[{"id":1,"name":"Drama"}],"popularity":99.5,
         "seasons":[{"season_number":0,"name":"Specials","episode_count":9,"id":5},{"season_number":1,"name":"Season 1","episode_count":10,"id":6}],
         "next_episode_to_air":{"season_number":9,"episode_number":3,"air_date":"2030-01-02","name":"Three","id":77,"runtime":50}}
        """;

    private const string MovieDetails = """
        {"id":603,"title":"The Matrix","original_title":"The Matrix","overview":"A hacker.","tagline":"Welcome.","genres":[{"id":28}],"runtime":136}
        """;

    private const string SeasonBody = """
        {"_id":"x","season_number":1,"name":"Season 1","overview":"First.","poster_path":"/p.jpg",
         "episodes":[{"id":1,"season_number":1,"episode_number":1,"name":"Winter","overview":"Ned.","air_date":"2011-04-17","runtime":62,
                      "still_path":"/s.jpg","vote_average":8.1,"vote_count":300,"production_code":"101",
                      "crew":[{"name":"Tim","job":"Director"}],"guest_stars":[{"name":"Ian","character":"Lord"}]}]}
        """;

    private const string RecommendationsBody = """
        {"page":2,"results":[{"id":5,"media_type":"tv","name":"Other","poster_path":"/o.jpg","genre_ids":[18]}],"total_pages":7,"total_results":130}
        """;

    [Fact]
    public async Task ASeriesComesBackWithTheListedFieldsOnly()
    {
        var tmdb = new FakeTmdb(TvDetails);
        var result = await Service().DetailsAsync(tmdb.Client, Key, "tv", "1399", "en-US", default);

        Assert.Equal(200, result.Status);
        using var doc = JsonDocument.Parse(result.Json);
        var root = doc.RootElement;
        Assert.Equal(1399, root.GetProperty("id").GetInt32());
        Assert.Equal("Game of Thrones", root.GetProperty("name").GetString());
        Assert.Equal("Nine noble families.", root.GetProperty("overview").GetString());
        Assert.Equal("Winter is coming.", root.GetProperty("tagline").GetString());
        Assert.False(root.TryGetProperty("genres", out _));
        Assert.False(root.TryGetProperty("popularity", out _));

        var seasons = root.GetProperty("seasons").EnumerateArray().ToList();
        Assert.Equal(2, seasons.Count);
        Assert.Equal("Season 1", seasons[1].GetProperty("name").GetString());
        Assert.False(seasons[1].TryGetProperty("episode_count", out _));

        var next = root.GetProperty("next_episode_to_air");
        Assert.Equal(9, next.GetProperty("season_number").GetInt32());
        Assert.Equal(3, next.GetProperty("episode_number").GetInt32());
        Assert.Equal("2030-01-02", next.GetProperty("air_date").GetString());
        Assert.Equal("Three", next.GetProperty("name").GetString());
        Assert.False(next.TryGetProperty("runtime", out _));
    }

    [Fact]
    public async Task ASeriesWithNothingToAirHasNullNextEpisode()
    {
        var tmdb = new FakeTmdb("""{"id":1,"name":"Done","overview":"","seasons":[],"next_episode_to_air":null}""");
        var result = await Service().DetailsAsync(tmdb.Client, Key, "tv", "1", null, default);

        using var doc = JsonDocument.Parse(result.Json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("next_episode_to_air").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("seasons").GetArrayLength());
    }

    [Fact]
    public async Task AMovieHasNoSeasonsAndNoNextEpisode()
    {
        var tmdb = new FakeTmdb(MovieDetails);
        var result = await Service().DetailsAsync(tmdb.Client, Key, "movie", "603", "en-US", default);

        Assert.Equal(200, result.Status);
        using var doc = JsonDocument.Parse(result.Json);
        var root = doc.RootElement;
        Assert.Equal("The Matrix", root.GetProperty("title").GetString());
        Assert.Equal("The Matrix", root.GetProperty("original_title").GetString());
        Assert.Equal("Welcome.", root.GetProperty("tagline").GetString());
        Assert.False(root.TryGetProperty("seasons", out _));
        Assert.False(root.TryGetProperty("next_episode_to_air", out _));
        Assert.Contains("/3/movie/603?", tmdb.Requests.Single());
    }

    [Fact]
    public async Task AnEmptyTranslationStaysEmpty()
    {
        var tmdb = new FakeTmdb("""{"id":1,"title":"T","overview":"","tagline":""}""");
        var result = await Service().DetailsAsync(tmdb.Client, Key, "movie", "1", "sv-SE", default);

        using var doc = JsonDocument.Parse(result.Json);
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("overview").GetString());
        Assert.Single(tmdb.Requests);
    }

    [Theory]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("de", "de")]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    [InlineData("../../etc&x=1", "en-US")]
    public async Task TheLanguageGoesThroughTheSameCheckAsReviews(string? asked, string sent)
    {
        var tmdb = new FakeTmdb(MovieDetails);
        await Service().DetailsAsync(tmdb.Client, Key, "movie", "603", asked, default);

        Assert.Contains($"language={sent}", tmdb.Requests.Single());
    }

    [Fact]
    public async Task ASeasonComesBackWithItsEpisodes()
    {
        var tmdb = new FakeTmdb(SeasonBody);
        var result = await Service().SeasonAsync(tmdb.Client, Key, "1399", 1, "en-US", default);

        Assert.Equal(200, result.Status);
        Assert.Contains("/3/tv/1399/season/1?", tmdb.Requests.Single());
        using var doc = JsonDocument.Parse(result.Json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("season_number").GetInt32());
        Assert.Equal("Season 1", root.GetProperty("name").GetString());
        Assert.Equal("First.", root.GetProperty("overview").GetString());
        Assert.False(root.TryGetProperty("poster_path", out _));

        var episode = root.GetProperty("episodes")[0];
        Assert.Equal(1, episode.GetProperty("episode_number").GetInt32());
        Assert.Equal("Winter", episode.GetProperty("name").GetString());
        Assert.Equal("Ned.", episode.GetProperty("overview").GetString());
        Assert.Equal("2011-04-17", episode.GetProperty("air_date").GetString());
        Assert.Equal(62, episode.GetProperty("runtime").GetInt32());
        Assert.Equal("/s.jpg", episode.GetProperty("still_path").GetString());
        Assert.Equal(8.1, episode.GetProperty("vote_average").GetDouble());
        Assert.Equal(300, episode.GetProperty("vote_count").GetInt32());
        Assert.Equal("Director", episode.GetProperty("crew")[0].GetProperty("job").GetString());
        Assert.Equal("Lord", episode.GetProperty("guest_stars")[0].GetProperty("character").GetString());
        Assert.False(episode.TryGetProperty("production_code", out _));
    }

    [Fact]
    public async Task SeasonZeroIsAllowed()
    {
        var tmdb = new FakeTmdb("""{"season_number":0,"name":"Specials","episodes":[]}""");
        var result = await Service().SeasonAsync(tmdb.Client, Key, "1399", 0, "en-US", default);

        Assert.Equal(200, result.Status);
        Assert.Contains("/season/0?", tmdb.Requests.Single());
    }

    [Fact]
    public async Task RecommendationsComeBackAsTmdbSendsThem()
    {
        var tmdb = new FakeTmdb(RecommendationsBody);
        var result = await Service().RecommendationsAsync(tmdb.Client, Key, "tv", "1399", "en-US", 2, default);

        Assert.Equal(200, result.Status);
        Assert.Contains("/3/tv/1399/recommendations?", tmdb.Requests.Single());
        Assert.Contains("page=2", tmdb.Requests.Single());
        using var doc = JsonDocument.Parse(result.Json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(7, root.GetProperty("total_pages").GetInt32());
        var first = root.GetProperty("results")[0];
        Assert.Equal("tv", first.GetProperty("media_type").GetString());
        Assert.Equal(18, first.GetProperty("genre_ids")[0].GetInt32());
        Assert.False(root.TryGetProperty("total_results", out _));
    }

    [Fact]
    public async Task TheKeyGoesToTmdbAsAV3QueryOrAV4Bearer()
    {
        var v3 = new FakeTmdb(MovieDetails);
        await Service().DetailsAsync(v3.Client, Key, "movie", "603", null, default);
        Assert.Contains($"api_key={Key}", v3.Requests.Single());

        var v4 = new FakeTmdb(MovieDetails);
        await Service().DetailsAsync(v4.Client, "eyJ.fake.token", "movie", "603", null, default);
        Assert.DoesNotContain("api_key", v4.Requests.Single());
        Assert.Equal("Bearer eyJ.fake.token", v4.Authorizations.Single());
    }

    [Theory]
    [InlineData(null, "603")]
    [InlineData("", "603")]
    [InlineData("show", "603")]
    [InlineData("movie", null)]
    [InlineData("movie", "")]
    [InlineData("movie", "abc")]
    [InlineData("movie", "0")]
    [InlineData("movie", "-5")]
    [InlineData("movie", "603/credits")]
    [InlineData("movie", "603?x=1")]
    public async Task DetailsRefusesABadParameter(string? mediaType, string? tmdbId)
    {
        var tmdb = new FakeTmdb(MovieDetails);
        var result = await Service().DetailsAsync(tmdb.Client, Key, mediaType, tmdbId, null, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(tmdb.Requests);
        AssertNoKey(result);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("abc", 1)]
    [InlineData("1399", null)]
    [InlineData("1399", -1)]
    public async Task SeasonRefusesABadParameter(string? tmdbId, int? season)
    {
        var tmdb = new FakeTmdb(SeasonBody);
        var result = await Service().SeasonAsync(tmdb.Client, Key, tmdbId, season, null, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(tmdb.Requests);
    }

    [Theory]
    [InlineData(null, "1399", 1)]
    [InlineData("tv", null, 1)]
    [InlineData("tv", "1399", 0)]
    [InlineData("tv", "1399", 501)]
    public async Task RecommendationsRefusesABadParameter(string? mediaType, string? tmdbId, int page)
    {
        var tmdb = new FakeTmdb(RecommendationsBody);
        var result = await Service().RecommendationsAsync(tmdb.Client, Key, mediaType, tmdbId, null, page, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(tmdb.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAServerKeyEveryRouteAnswers503AndSaysNothingOfKeys(string? key)
    {
        var tmdb = new FakeTmdb(MovieDetails);
        var service = Service();
        var answers = new[]
        {
            await service.DetailsAsync(tmdb.Client, key, "movie", "603", null, default),
            await service.SeasonAsync(tmdb.Client, key, "1399", 1, null, default),
            await service.RecommendationsAsync(tmdb.Client, key, "tv", "1399", null, 1, default),
        };

        Assert.All(answers, a => Assert.Equal(503, a.Status));
        Assert.All(answers, a => Assert.DoesNotContain("key", a.Json, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(tmdb.Requests);
    }

    [Fact]
    public async Task AnIdTmdbDoesNotKnowAnswers404()
    {
        var tmdb = new FakeTmdb(HttpStatusCode.NotFound, """{"success":false,"status_code":34}""");
        var service = Service();

        Assert.Equal(404, (await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default)).Status);
        Assert.Equal(404, (await service.SeasonAsync(tmdb.Client, Key, "1", 99, null, default)).Status);
        Assert.Equal(404, (await service.RecommendationsAsync(tmdb.Client, Key, "tv", "1", null, 1, default)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task WhenTmdbFailsTheAnswerIs502(HttpStatusCode status)
    {
        var tmdb = new FakeTmdb(status, "{}");
        var log = new CapturingLogger();
        var result = await Service(log).DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);

        Assert.Equal(502, result.Status);
        AssertNoKey(result, log);
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("{\"hello\":\"world\"}")]
    [InlineData("")]
    public async Task AnswerThatIsNotTmdbsIs502(string body)
    {
        var log = new CapturingLogger();
        var service = Service(log);
        var tmdb = new FakeTmdb(body);

        Assert.Equal(502, (await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default)).Status);
        Assert.Equal(502, (await service.SeasonAsync(tmdb.Client, Key, "1", 1, null, default)).Status);
        Assert.Equal(502, (await service.RecommendationsAsync(tmdb.Client, Key, "movie", "1", null, 1, default)).Status);
        Assert.All(log.Lines, l => Assert.DoesNotContain(Key, l));
    }

    [Fact]
    public async Task ElementsOfTheWrongTypeAre502NotACrash()
    {
        var service = Service();
        var details = new FakeTmdb("""{"id":1,"name":"x","seasons":[1,2]}""");
        var season = new FakeTmdb("""{"season_number":1,"episodes":["x"]}""");
        var details2 = new FakeTmdb("""{"id":1,"seasons":[{"season_number":1,"name":"s"}],"next_episode_to_air":7}""");

        Assert.Equal(502, (await service.DetailsAsync(details.Client, Key, "tv", "1", null, default)).Status);
        Assert.Equal(502, (await service.SeasonAsync(season.Client, Key, "1", 1, null, default)).Status);
        Assert.Equal(200, (await service.DetailsAsync(details2.Client, Key, "tv", "2", null, default)).Status);
    }

    [Fact]
    public async Task ANetworkFailureIs502AndTheLogHasNoAddress()
    {
        var tmdb = new FakeTmdb(_ => throw new HttpRequestException($"could not reach https://api.themoviedb.org/3/movie/1?api_key={Key}"));
        var log = new CapturingLogger();
        var result = await Service(log).DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);

        Assert.Equal(502, result.Status);
        Assert.NotEmpty(log.Lines);
        AssertNoKey(result, log);
    }

    [Fact]
    public async Task ATimeoutIs502ButACancelledRequestStaysCancelled()
    {
        var timeout = new FakeTmdb(_ => throw new TaskCanceledException("timed out"));
        Assert.Equal(502, (await Service().DetailsAsync(timeout.Client, Key, "movie", "1", null, default)).Status);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var tmdb = new FakeTmdb(MovieDetails);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().DetailsAsync(tmdb.Client, Key, "movie", "1", null, cancelled.Token));
    }

    [Fact]
    public async Task TheSecondCallDoesNotReachTmdb()
    {
        var tmdb = new FakeTmdb(_ => Ok(MovieDetails));
        var service = Service();
        var first = await service.DetailsAsync(tmdb.Client, Key, "movie", "603", "en-US", default);
        var second = await service.DetailsAsync(tmdb.Client, Key, "movie", "603", "en-US", default);

        Assert.Equal(first.Json, second.Json);
        Assert.Single(tmdb.Requests);
    }

    [Fact]
    public async Task EachRouteKeepsItsOwnAnswerPerIdLanguageSeasonAndPage()
    {
        var tmdb = new FakeTmdb(req => Ok(
            req.RequestUri!.AbsolutePath.Contains("/season/") ? SeasonBody
            : req.RequestUri.AbsolutePath.Contains("/recommendations") ? RecommendationsBody
            : TvDetails));
        var service = Service();

        await service.DetailsAsync(tmdb.Client, Key, "tv", "1399", "en-US", default);
        await service.DetailsAsync(tmdb.Client, Key, "tv", "1399", "fr-FR", default);
        await service.DetailsAsync(tmdb.Client, Key, "tv", "1400", "en-US", default);
        await service.SeasonAsync(tmdb.Client, Key, "1399", 1, "en-US", default);
        await service.SeasonAsync(tmdb.Client, Key, "1399", 2, "en-US", default);
        await service.RecommendationsAsync(tmdb.Client, Key, "tv", "1399", "en-US", 1, default);
        await service.RecommendationsAsync(tmdb.Client, Key, "tv", "1399", "en-US", 2, default);
        Assert.Equal(7, tmdb.Requests.Count);

        await service.DetailsAsync(tmdb.Client, Key, "tv", "1399", "fr-FR", default);
        await service.SeasonAsync(tmdb.Client, Key, "1399", 2, "en-US", default);
        await service.RecommendationsAsync(tmdb.Client, Key, "tv", "1399", "en-US", 2, default);
        Assert.Equal(7, tmdb.Requests.Count);
    }

    [Fact]
    public async Task AFailureIsNotRemembered()
    {
        var calls = 0;
        var tmdb = new FakeTmdb(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Ok(MovieDetails));
        var service = Service();

        Assert.Equal(502, (await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default)).Status);
        Assert.Equal(200, (await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default)).Status);
    }

    [Fact]
    public async Task AnAnswerIsKeptForADayAndThenAskedAgain()
    {
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var tmdb = new FakeTmdb(_ => Ok(MovieDetails));
        var service = new TmdbProxyService(new CapturingLogger(), 500, () => now);

        await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);
        now += TimeSpan.FromHours(23);
        await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);
        Assert.Single(tmdb.Requests);

        now += TimeSpan.FromHours(2);
        await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);
        Assert.Equal(2, tmdb.Requests.Count);
    }

    [Fact]
    public async Task WhenTheCacheIsFullTheOldestAnswerGoes()
    {
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var tmdb = new FakeTmdb(_ => Ok(MovieDetails));
        var service = new TmdbProxyService(new CapturingLogger(), 2, () => now);

        foreach (var id in new[] { "1", "2", "3" })
        {
            await service.DetailsAsync(tmdb.Client, Key, "movie", id, null, default);
            now += TimeSpan.FromMinutes(1);
        }

        Assert.Equal(3, tmdb.Requests.Count);
        await service.DetailsAsync(tmdb.Client, Key, "movie", "2", null, default);
        await service.DetailsAsync(tmdb.Client, Key, "movie", "3", null, default);
        Assert.Equal(3, tmdb.Requests.Count);
        await service.DetailsAsync(tmdb.Client, Key, "movie", "1", null, default);
        Assert.Equal(4, tmdb.Requests.Count);
    }

    [Fact]
    public async Task TheKeyIsInNoAnswerAndNoLogLine()
    {
        var log = new CapturingLogger();
        var service = Service(log);
        var results = new List<TmdbProxyResult>();

        foreach (var body in new[] { MovieDetails, "not json", "{}" })
        {
            var tmdb = new FakeTmdb(body);
            results.Add(await service.DetailsAsync(tmdb.Client, Key, "movie", "9", "en-US", default));
        }

        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized })
        {
            var tmdb = new FakeTmdb(status, "{}");
            results.Add(await service.SeasonAsync(tmdb.Client, Key, "9", 1, "en-US", default));
            results.Add(await service.RecommendationsAsync(tmdb.Client, Key, "tv", "9", "en-US", 1, default));
        }

        results.Add(await service.DetailsAsync(new FakeTmdb(MovieDetails).Client, Key, "bogus", "9", null, default));
        results.Add(await service.DetailsAsync(new FakeTmdb(MovieDetails).Client, null, "movie", "9", null, default));

        Assert.Contains(results, r => r.Status == 200);
        Assert.NotEmpty(log.Lines);
        foreach (var result in results)
        {
            AssertNoKey(result, log);
        }
    }

    [Theory]
    [InlineData(nameof(Api.TmdbController.GetDetails), "Details")]
    [InlineData(nameof(Api.TmdbController.GetSeason), "Season")]
    [InlineData(nameof(Api.TmdbController.GetRecommendations), "Recommendations")]
    public void EachRouteIsAnAuthorizedGetUnderMoonfinTmdb(string action, string route)
    {
        var method = typeof(Api.TmdbController).GetMethod(action)!;

        Assert.Equal(route, method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false).Cast<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().Single().Template);
        Assert.NotEmpty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false));
        Assert.Equal("Moonfin/Tmdb", typeof(Api.TmdbController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false).Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>().Single().Template);
    }

    [Fact]
    public void TheServiceCanBeBuiltByTheHost()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TmdbProxyService>(services);

        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TmdbProxyService>(provider));
    }

    private static TmdbProxyService Service(CapturingLogger? log = null) => new(log ?? new CapturingLogger(), 500, () => DateTimeOffset.UtcNow);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static void AssertNoKey(TmdbProxyResult result, CapturingLogger? log = null)
    {
        Assert.DoesNotContain(Key, result.Json);
        Assert.All(log?.Lines ?? new List<string>(), l => Assert.DoesNotContain(Key, l));
    }

    private sealed class FakeTmdb
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public FakeTmdb(string body) : this(_ => Ok(body)) { }

        public FakeTmdb(HttpStatusCode status, string body)
            : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }) { }

        public FakeTmdb(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _answer = answer;
            Client = new HttpClient(new Handler(this));
        }

        public HttpClient Client { get; }

        public List<string> Requests { get; } = new();

        public List<string?> Authorizations { get; } = new();

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FakeTmdb _owner;

            public Handler(FakeTmdb owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _owner.Requests.Add(request.RequestUri!.ToString());
                _owner.Authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(_owner._answer(request));
            }
        }
    }

    private sealed class CapturingLogger : ILogger<TmdbProxyService>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception != null)
            {
                Lines.Add(exception.ToString());
            }
        }
    }
}
