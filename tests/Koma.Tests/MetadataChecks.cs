using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Koma;

internal static class MetadataChecks
{
    public static void Run(string scratch)
    {
        CheckParser(); CheckClients().GetAwaiter().GetResult();
    }

    private static void CheckParser()
    {
        using var valid = JsonDocument.Parse("""
            {"id":123,"title":"Hoshi no Monogatari","alternative_titles":{"en":"Star Story","ja":"星物語","synonyms":["StarStory","StarStory"]},"start_season":{"year":2024},"media_type":"tv","num_episodes":12,"main_picture":{"medium":"https://cdn.myanimelist.net/images/anime/1/123.jpg"},"synopsis":"A story.\nA second line."}
            """);
        var candidate = MalCandidateMetadata.Read(valid.RootElement, out bool incomplete);
        Require(candidate is { Id: 123, EnglishTitle: "Star Story", StartYear: 2024, MediaType: "tv", EpisodeCount: 12 } &&
            candidate.PictureUrl == "https://cdn.myanimelist.net/images/anime/1/123.jpg" &&
            candidate.Synopsis == "A story. A second line." && candidate.AlternativeTitles.SequenceEqual(new[] { "Star Story", "星物語", "StarStory" }) && !incomplete,
            "Candidate summaries should preserve bounded matching aliases and useful visual metadata.");
        foreach (string picture in new[] { "http://cdn.myanimelist.net/images/anime/1/123.jpg", "https://attacker.test/images/anime/1/123.jpg",
            "https://cdn.myanimelist.net.evil.test/images/anime/1/123.jpg", "https://person:secret@cdn.myanimelist.net/images/anime/1/123.jpg",
            "https://cdn.myanimelist.net:444/images/anime/1/123.jpg", "https://cdn.myanimelist.net/private/path.jpg" })
        {
            using var unsafeImage = JsonDocument.Parse(JsonSerializer.Serialize(new { id = 1, title = "Example", main_picture = new { medium = picture } }));
            Require(MalCandidateMetadata.Read(unsafeImage.RootElement, out _)?.PictureUrl is null,
                "Cover images must be limited to the official HTTPS anime image hosts and paths.");
        }
        using var malformed = JsonDocument.Parse("""
            {"id":123,"title":"Example","alternative_titles":{"en":["bad"],"synonyms":["Good",42]},"num_episodes":"unknown","start_season":{"year":"unknown"}}
            """);
        Require(MalCandidateMetadata.Read(malformed.RootElement, out bool damaged) is { SearchIncomplete: true, EpisodeCount: 0 } && damaged,
            "Damaged aliases or numbering metadata must not establish an automatic match.");
        using var invalid = JsonDocument.Parse("{\"id\":0,\"title\":\"bad\"}");
        Require(MalCandidateMetadata.Read(invalid.RootElement, out damaged) is null && damaged,
            "Unusable identities must be rejected.");
        using var longSynopsis = JsonDocument.Parse(JsonSerializer.Serialize(new { id = 1, title = "Example", synopsis = new string('x', 3000) }));
        Require(MalCandidateMetadata.Read(longSynopsis.RootElement, out _)!.Synopsis.Length == 600,
            "Synopsis summaries must be bounded before use in the chooser.");
    }

    private static async Task CheckClients()
    {
        const string node = """
            {"id":123,"title":"Hoshi no Monogatari","alternative_titles":{"en":"Star Story","synonyms":[]},"media_type":"tv","start_season":{"year":2024},"num_episodes":12,"main_picture":{"medium":"https://images.myanimelist.net/images/anime/1/123.jpg"},"synopsis":"A short synopsis."}
            """;
        int catalogueCalls = 0, listCalls = 0;
        using var handler = new Handler((request, _) => {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
            Require(uri is { Scheme: "https", Host: "api.myanimelist.net" }, "Metadata requests must use the fixed official API host.");
            string query = Uri.UnescapeDataString(uri.Query);
            Require(query.Contains("alternative_titles") && query.Contains("main_picture") && query.Contains("media_type"),
                "The API should request the alias and visual fields used by the chooser.");
            if (uri.AbsolutePath == "/v2/anime")
            {
                Require(query.Contains("synopsis"), "Small catalogue result sets should include chooser summaries.");
                catalogueCalls++;
                Require(request.Headers.Authorization is null && request.Headers.Contains("X-MAL-CLIENT-ID"), "Catalogue metadata must not carry account tokens.");
                return Task.FromResult(Json("{\"data\":[{\"node\":" + node + "}]}"));
            }
            listCalls++;
            Require(!query.Contains("synopsis"), "List paging should avoid large synopsis payloads while retaining titles and covers.");
            Require(request.Headers.Authorization?.Parameter == "synthetic-account-token" && !request.Headers.Contains("X-MAL-CLIENT-ID"),
                "Account list reads should retain their own authentication path.");
            return Task.FromResult(Json("{\"data\":[{\"node\":" + node + ",\"list_status\":{\"status\":\"watching\",\"num_episodes_watched\":4}}],\"paging\":{}}"));
        });
        using var http = new HttpClient(handler);
        var search = await new MalSearchClient(http, "synthetic-client-id").SearchAsync("Star Story", default);
        var page = await new MalAccountClient(http, (_, _) => Task.FromResult("synthetic-account-token")).GetListAsync(null, 0, default);
        Require(catalogueCalls == 1 && listCalls == 1 && page.NextOffset is null &&
            page.Entries.Single().Candidate is { EnglishTitle: "Star Story", PictureUrl: not null, Synopsis: "A short synopsis." } &&
            search.Single().Id == page.Entries.Single().Candidate!.Id &&
            search.Single().AlternativeTitles.SequenceEqual(page.Entries.Single().Candidate!.AlternativeTitles) &&
            search.Single().EnglishTitle == page.Entries.Single().Candidate!.EnglishTitle,
            "Catalogue and account-list matching metadata must use the same parser.");

        using var partialHandler = new Handler((_, _) => Task.FromResult(Json("""
            {"data":[{"node":{"id":1,"title":"First","media_type":"tv","num_episodes":12},"list_status":{"status":"watching","num_episodes_watched":0}},{"node":{"id":2,"title":"Second","alternative_titles":{"en":42}},"list_status":{"status":"watching","num_episodes_watched":0}}],"paging":{}}
            """)));
        using var partialHttp = new HttpClient(partialHandler);
        var damagedPage = await new MalAccountClient(partialHttp, (_, _) => Task.FromResult("synthetic-account-token")).GetListAsync(null, 0, default);
        Require(damagedPage.Entries.All(x => x.Candidate is { SearchIncomplete: true }),
            "A malformed matching node must not make its remaining list page look unambiguous.");
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
