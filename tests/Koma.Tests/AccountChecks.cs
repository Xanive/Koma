using System.Net;
using System.Net.Http;
using System.Text;
using Koma;

internal static class AccountChecks
{
    public static void Run(string scratch)
    {
        CheckCallback();
        CheckAccount().GetAwaiter().GetResult();
    }

    private static void CheckCallback()
    {
        var flow = new MalOAuthFlow();
        var uri = flow.AuthorizationUri("synthetic-client-id-123456");
        Require(uri.Scheme == "https" && uri.Host == "myanimelist.net" && uri.AbsolutePath == "/v1/oauth2/authorize", "Authorization must use the official HTTPS endpoint.");
        var query = Query(uri.Query);
        Require(query["redirect_uri"] == MalOAuthFlow.RedirectUri && query["code_challenge"] == flow.Verifier &&
            query["code_challenge_method"] == "plain" && flow.Verifier.Length >= 43, "The registered loopback redirect and per-login PKCE verifier must be used.");
        var other = Query(new MalOAuthFlow().AuthorizationUri("synthetic-client-id-123456").Query);
        Require(query["state"] != other["state"] && query["code_challenge"] != other["code_challenge"], "Each login needs an independent state and verifier.");
        string callback = "GET /callback?code=synthetic-code&state=" + query["state"] + " HTTP/1.1\r\nHost: 127.0.0.1:43857\r\n\r\n";
        Require(flow.TryReadCallback(callback, out var code, out bool denied) && code == "synthetic-code" && !denied, "A matching browser callback should complete authorization.");
        foreach (string invalid in new[] {
            callback.Replace(query["state"], other["state"]), callback.Replace("Host: 127.0.0.1:43857", "Host: attacker.test"),
            callback.Replace("GET ", "POST "), callback.Replace("/callback?", "/other?"),
            callback.Replace("&state=", "&code=duplicate&state="),
            callback.Replace(" HTTP/1.1", "&state=" + query["state"] + " HTTP/1.1"),
            callback.Replace("synthetic-code", "%0d%0aInjected"), callback.Replace("Host: ", "Host: bad.test\r\nHost: ")
        }) Require(!flow.TryReadCallback(invalid, out _, out _), "Malformed, duplicate or cross-session callbacks must be rejected.");
        string decline = callback.Replace("code=synthetic-code", "error=access_denied");
        Require(flow.TryReadCallback(decline, out _, out denied) && denied, "An explicit user decline should end the pending connection without tokens.");
    }

    private static async Task CheckAccount()
    {
        int refreshes = 0;
        Task<string> Token(bool refresh, CancellationToken _) { if (refresh) refreshes++; return Task.FromResult("synthetic-access-token"); }
        var requests = new List<(string Path, HttpMethod Method, string Body)>();
        using var handler = new Handler(async (request, _) => {
            Require(request.RequestUri!.Host == "api.myanimelist.net" && request.RequestUri.Scheme == "https", "Account tokens must only be sent to the fixed official API host.");
            Require(request.Headers.Authorization?.Parameter == "synthetic-access-token" && !request.RequestUri.ToString().Contains("synthetic-access-token"), "Access tokens belong in the Authorization header.");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request.RequestUri.PathAndQuery, request.Method, body));
            if (request.RequestUri.AbsolutePath == "/v2/users/@me") return Json("{\"id\":123,\"name\":\"Example\"}");
            if (request.RequestUri.AbsolutePath == "/v2/users/@me/animelist") return Json("""
                {"data":[{"node":{"id":42,"title":"A synthetic series","num_episodes":12},"list_status":{"status":"watching","num_episodes_watched":4,"is_rewatching":false}}],"paging":{"next":"https://attacker.test/steal-token"}}
                """);
            if (request.Method == HttpMethod.Patch) return Json(Status(5));
            return Json("{\"id\":42,\"num_episodes\":12,\"my_list_status\":" + Status(4) + "}");
        });
        using var http = new HttpClient(handler);
        var client = new MalAccountClient(http, Token);
        Require((await client.GetProfileAsync(default)).Name == "Example", "The connected profile should parse.");
        var page = await client.GetListAsync("watching", 100, default);
        Require(page.Entries.Single().Status.WatchedEpisodes == 4 && page.NextOffset == 101, "List pages should parse progress and derive their next offset without following remote links.");
        var updated = await client.UpdateProgressAsync(42, 5, 4, default);
        Require(updated.WatchedEpisodes == 5 && requests.Last().Body == "num_watched_episodes=5", "A progress update must preserve unrelated status, scores, dates and comments.");
        int before = requests.Count;
        await Throws<InvalidOperationException>(() => client.UpdateProgressAsync(42, 6, 3, default));
        Require(requests.Count == before + 1 && requests.Last().Method == HttpMethod.Get, "Changed remote progress must block the write.");
        await Throws<ArgumentException>(() => client.GetListAsync("invalid", 0, default));
        await Throws<ArgumentOutOfRangeException>(() => client.UpdateProgressAsync(42, 2, 4, default));
        await Throws<ArgumentOutOfRangeException>(() => client.GetListAsync(null, -1, default));

        foreach (string status in new[] { "on_hold", "dropped", "completed" })
        {
            int writes = 0;
            using var blockedHandler = new Handler((request, _) => {
                if (request.Method == HttpMethod.Patch) writes++;
                return Task.FromResult(Json("{\"num_episodes\":12,\"my_list_status\":" + Status(4, status) + "}"));
            });
            using var blockedHttp = new HttpClient(blockedHandler);
            await Throws<InvalidOperationException>(() => new MalAccountClient(blockedHttp, Token).UpdateProgressAsync(42, 5, 4, default));
            Require(writes == 0, "Held, dropped or completed series need explicit review before automatic progress.");
        }

        using var rewatchHandler = new Handler((_, _) => Task.FromResult(Json("{\"num_episodes\":12,\"my_list_status\":" + Status(4).Replace("false", "true") + "}")));
        using var rewatchHttp = new HttpClient(rewatchHandler);
        await Throws<InvalidOperationException>(() => new MalAccountClient(rewatchHttp, Token).UpdateProgressAsync(42, 5, 4, default));

        int missingWrites = 0;
        using var missingHandler = new Handler((request, _) => {
            if (request.Method == HttpMethod.Patch) missingWrites++;
            return Task.FromResult(Json("{\"num_episodes\":12}"));
        });
        using var missingHttp = new HttpClient(missingHandler);
        await Throws<InvalidOperationException>(() => new MalAccountClient(missingHttp, Token).UpdateProgressAsync(42, 1, 0, default, expectedExists: true));
        Require(missingWrites == 0, "Deleting a previously present zero-progress entry must block silent recreation.");

        using var cancelled = new CancellationTokenSource();
        int cancelledWrites = 0;
        using var cancelHandler = new Handler((request, _) => {
            if (request.Method == HttpMethod.Patch) cancelledWrites++;
            cancelled.Cancel();
            return Task.FromResult(Json("{\"num_episodes\":12,\"my_list_status\":" + Status(4) + "}"));
        });
        using var cancelHttp = new HttpClient(cancelHandler);
        await Throws<OperationCanceledException>(() => new MalAccountClient(cancelHttp, Token).UpdateProgressAsync(42, 5, 4, cancelled.Token));
        Require(cancelledWrites == 0, "Cancellation after reconciliation must stop the pending progress write.");

        int patchAttempts = 0;
        using var uncertainHandler = new Handler((request, _) => {
            if (request.Method == HttpMethod.Patch) { patchAttempts++; throw new HttpRequestException("Synthetic interrupted write"); }
            return Task.FromResult(Json("{\"num_episodes\":12,\"my_list_status\":" + Status(4) + "}"));
        });
        using var uncertainHttp = new HttpClient(uncertainHandler);
        await Throws<HttpRequestException>(() => new MalAccountClient(uncertainHttp, Token).UpdateProgressAsync(42, 5, 4, default));
        Require(patchAttempts == 1, "An interrupted PATCH must not automatically replay.");

        int attempts = 0;
        using var retryHandler = new Handler((_, _) => Task.FromResult(++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json("{\"id\":123,\"name\":\"Example\"}")));
        using var retryHttp = new HttpClient(retryHandler);
        await new MalAccountClient(retryHttp, Token).GetProfileAsync(default);
        Require(attempts == 2 && refreshes == 1, "An explicitly rejected access token should get one refresh attempt.");
        foreach (HttpStatusCode status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect, HttpStatusCode.ServiceUnavailable })
        {
            int count = 0;
            using var errorHandler = new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(status)); });
            using var errorHttp = new HttpClient(errorHandler);
            await Throws<HttpRequestException>(() => new MalAccountClient(errorHttp, Token).GetProfileAsync(default));
            Require(count == 1, "Throttling, redirects and server errors must not create retry storms.");
        }
        using var largeHandler = new Handler((_, _) => Task.FromResult(Json(new string('x', 1024 * 1024 + 1))));
        using var largeHttp = new HttpClient(largeHandler);
        await Throws<HttpRequestException>(() => new MalAccountClient(largeHttp, Token).GetProfileAsync(default));
    }

    private static string Status(int watched, string status = "watching") =>
        $"{{\"status\":\"{status}\",\"num_episodes_watched\":{watched},\"is_rewatching\":false}}";
    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
