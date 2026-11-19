using System.Net;
using System.Net.Http;
using System.Text;
using Koma;

internal static class MatchingChecks
{
    public static void Run(string scratch)
    {
        CheckRanking();
        CheckSearch().GetAwaiter().GetResult();
    }

    private static void CheckRanking()
    {
        var detection = new DetectedAnime("Frieren", null, 7, AnimeKind.Episode, false);
        var first = new MalAnimeCandidate(1, "Sousou no Frieren", ["Frieren"], 2023, "tv", 28);
        Require(AnimeMatcher.Rank(detection, [first]).AutomaticMatch?.Id == 1, "A unique exact alias should match.");
        Require(AnimeMatcher.Rank(detection, [first, first with { Id = 2, StartYear = 2040 }]).AutomaticMatch is null,
            "Two entries with the same exact alias need review, including remakes.");
        Require(AnimeMatcher.Rank(detection with { Title = "Frieran" }, [first]).AutomaticMatch is null,
            "A fuzzy title must never link automatically.");
        var season = new MalAnimeCandidate(3, "Seaside Story", [], 2020, "tv", 12);
        Require(AnimeMatcher.Rank(new("Seaside Story", 2, 3, AnimeKind.Episode, false), [season]).AutomaticMatch is null,
            "A second-season folder must not link to a first-season entry.");
        Require(AnimeMatcher.Rank(new("Seaside Story Season 2", 2, 3, AnimeKind.Episode, false),
            [season with { Title = "Seaside Story Season 2" }]).AutomaticMatch?.Id == 3,
            "An exact title with an explicit agreeing season can match.");
        Require(AnimeMatcher.Rank(detection with { NeedsReview = true }, [first]).AutomaticMatch is null,
            "An uncertain parse requires review.");
        Require(AnimeMatcher.Rank(detection with { Kind = AnimeKind.Special }, [first]).AutomaticMatch is null,
            "Specials must not become normal episodes.");
        Require(AnimeMatcher.Rank(detection with { Episode = 29 }, [first]).AutomaticMatch is null,
            "A known out-of-range episode must not link automatically.");
        Require(AnimeMatcher.Rank(detection, [first with { SearchIncomplete = true }]).AutomaticMatch is null,
            "Truncated catalog results cannot establish a unique match.");
        Require(AnimeMatcher.Rank(detection, [first with { MediaType = "unknown" }]).AutomaticMatch is null,
            "Missing provider type data must not silently link an episode to a possible movie or special.");
        Require(AnimeMatcher.Rank(detection with { Title = "Anime" }, [first with { Title = "Anime" }]).AutomaticMatch is null,
            "A generic collection label is not a safe identity.");
        Require(AnimeMatcher.Rank(new("86", null, 1, AnimeKind.Episode, false),
            [new(8, "8", [], null, "tv", 12), new(86, "86", [], null, "tv", 12)]).AutomaticMatch?.Id == 86,
            "Numbers in titles must stay distinct.");
        Require(AnimeMatcher.Rank(new("UniqueAnimeLongName", null, 1, AnimeKind.Episode, false),
            [new(1, "UniqueAnimeLongName", [], null, "tv", 12), new(2, "UniqueAnimeLongNames", [], null, "tv", 12)])
            .AutomaticMatch is null, "A very close alternative should require review.");
        Require(AnimeMatcher.Rank(detection with { Kind = AnimeKind.Movie }, [first]).AutomaticMatch is null,
            "A movie should not silently match a television entry.");
        Require(AnimeMatcher.Rank(detection with { Kind = AnimeKind.Movie, Episode = null },
            [first with { MediaType = "movie" }]).AutomaticMatch?.Id == 1, "A clearly matching movie can match.");
        Require(AnimeMatcher.Rank(detection with { Title = "" }, [first]).AutomaticMatch is null,
            "An empty title cannot match.");
        Require(AnimeMatcher.Rank(detection, [first, first]).RankedCandidates.Count == 1,
            "Repeated IDs should not duplicate the review choices.");
    }

    private static async Task CheckSearch()
    {
        const string body = """
            {"data":[{"node":{"id":1,"title":"Fate/stay night & Friends","alternative_titles":{"en":"English name","ja":"日本語","synonyms":["Alias","Alias"]},"start_season":{"year":2023},"num_episodes":12,"media_type":"tv"}}]}
            """;
        using var handler = new FakeHandler((request, token) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        var client = new MalSearchClient(http, "test-client");
        var result = await client.SearchAsync("Fate/stay night & Friends", CancellationToken.None);
        Require(handler.Calls == 1 && handler.Method == HttpMethod.Get, "Catalog searches should make a single read-only request.");
        Require(handler.Uri?.Host == "api.myanimelist.net" && handler.Uri.AbsolutePath == "/v2/anime",
            "Queries must use the fixed official HTTPS API host.");
        Require(handler.Uri?.Scheme == "https" && handler.Uri.OriginalString.Contains("Fate%2Fstay%20night%20%26%20Friends"),
            "Titles must be query-escaped rather than concatenated as URL parameters.");
        Require(handler.ClientId == "test-client" && !handler.Uri!.OriginalString.Contains("test-client") && !handler.HasAuthorization,
            "The app ID belongs in the request header, without account tokens or URL credentials.");
        Require(result.Count == 1 && result[0].StartYear == 2023 && result[0].EpisodeCount == 12 &&
            result[0].AlternativeTitles.SequenceEqual(new[] { "English name", "日本語", "Alias" }),
            "Candidate data and alternate titles should parse and deduplicate.");

        int prior = handler.Calls;
        foreach (string query in new[] { "", "  ", @"C:\Users\person\Anime\Episode 01.mkv", "/home/person/Anime", "https://example.test", "Episode.mkv", "name\nsecret", "../private", new string('a', 201) })
            await Throws<ArgumentException>(() => client.SearchAsync(query, CancellationToken.None));
        Require(handler.Calls == prior, "Rejected path-like queries must never reach the network.");
        ThrowsSync<ArgumentException>(() => _ = new MalSearchClient(http, "invalid\r\nheader"));

        using var incompleteHandler = new FakeHandler((_, _) => Task.FromResult(Json(body[..^1] + ",\"paging\":{\"next\":\"https://api.myanimelist.net/v2/anime?offset=10\"}}")));
        using var incompleteHttp = new HttpClient(incompleteHandler);
        var partial = await new MalSearchClient(incompleteHttp, "test-client").SearchAsync("Fate", CancellationToken.None);
        Require(partial.Single().SearchIncomplete, "Further search pages must suppress automatic linking.");

        const string malformedNodes = """
            {"data":[{"node":{"id":"invalid","title":"bad"}},{"node":{"id":2,"title":"Good","num_episodes":"unknown","start_season":{"year":"unknown"}}}]}
            """;
        using var nodeHandler = new FakeHandler((_, _) => Task.FromResult(Json(malformedNodes)));
        using var nodeHttp = new HttpClient(nodeHandler);
        var valid = await new MalSearchClient(nodeHttp, "test-client").SearchAsync("Good", CancellationToken.None);
        Require(valid.Count == 1 && valid[0].Id == 2 && valid[0].SearchIncomplete,
            "Malformed candidates must not crash parsing or make the remaining match look unambiguous.");

        foreach (string invalid in new[] { "<html>unavailable</html>", "{}", "{\"data\":null}" })
        {
            using var invalidHandler = new FakeHandler((_, _) => Task.FromResult(Json(invalid)));
            using var invalidHttp = new HttpClient(invalidHandler);
            await Throws<HttpRequestException>(() => new MalSearchClient(invalidHttp, "test-client").SearchAsync("Title", CancellationToken.None));
        }

        foreach (HttpStatusCode status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect, HttpStatusCode.Forbidden })
        {
            using var errorHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
            using var errorHttp = new HttpClient(errorHandler);
            await Throws<HttpRequestException>(() => new MalSearchClient(errorHttp, "test-client").SearchAsync("Title", CancellationToken.None));
            Require(errorHandler.Calls == 1, "Errors, redirects and throttling must not trigger automatic retries.");
        }

        using var largeHandler = new FakeHandler((_, _) => Task.FromResult(Json(new string('x', 300000))));
        using var largeHttp = new HttpClient(largeHandler);
        await Throws<HttpRequestException>(() => new MalSearchClient(largeHttp, "test-client").SearchAsync("Title", CancellationToken.None));
        using var streamingHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnboundedContent() }));
        using var streamingHttp = new HttpClient(streamingHandler);
        await Throws<HttpRequestException>(() => new MalSearchClient(streamingHttp, "test-client").SearchAsync("Title", CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.SearchAsync("Title", cancelled.Token));
        using var offlineHandler = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        using var offlineHttp = new HttpClient(offlineHandler);
        await Throws<HttpRequestException>(() => new MalSearchClient(offlineHttp, "test-client").SearchAsync("Title", CancellationToken.None));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void ThrowsSync<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ClientId { get; private set; }
        public bool HasAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Method = request.Method;
            ClientId = request.Headers.TryGetValues("X-MAL-CLIENT-ID", out var values) ? values.Single() : null;
            HasAuthorization = request.Headers.Authorization is not null;
            return send(request, cancellationToken);
        }
    }

    private sealed class UnboundedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(new byte[300000]);
    }
}
