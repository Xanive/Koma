using System.Net;
using System.Net.Http;
using System.Text;
using Koma;

internal static class FeedChecks
{
    private static readonly Uri Feed = new("https://example.test/releases/rss?passkey=synthetic-private-token");
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    public static void Run(string scratch)
    {
        CheckSources(); CheckParse(); CheckFetch().GetAwaiter().GetResult();
    }

    private static void CheckSources()
    {
        Require(ReleaseSource.TryParseMagnet("magnet:?xt=urn:btih:" + Hash + "&dn=Example%20Anime&tr=https%3A%2F%2Ftracker.test%2Fannounce&xs=file%3A%2F%2FC%3A%2Fsecret&ws=http%3A%2F%2Funsafe.test%2F&unknown=bad", out var magnet, out var identity) &&
            identity == "urn:btih:" + Hash && magnet is not null && !magnet.Query.Contains("xs=") && !magnet.Query.Contains("ws=") && !magnet.Query.Contains("unknown="),
            "Feed-origin magnets should preserve a torrent identity and omit external metadata/webseed parameters.");
        Require(ReleaseSource.TryParseMagnet("magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", out _, out identity) && identity == "urn:btih:" + new string('0', 40),
            "Base32 info hashes should normalize to the same hex identity.");
        Require(ReleaseSource.TryParseMagnet("magnet:?xt=urn:btmh:1220" + new string('a', 64), out _, out identity) && identity.StartsWith("urn:btmh:1220"), "V2 SHA256 multihashes should be supported.");
        Require(!ReleaseSource.TryParseMagnet("magnet:?xt=urn:btih:" + Hash + "&xt=urn:btih:" + new string('f', 40), out _, out _),
            "Conflicting identities of the same hash version must not silently select a different torrent.");
        Require(ReleaseSource.TryParseMagnet("magnet:?xt=urn:btih:" + Hash + "&xt=urn:btmh:1220" + new string('a', 64), out _, out identity) && identity == "urn:btih:" + Hash,
            "A hybrid torrent can carry one identity for each supported hash version.");
        foreach (string invalid in new[] { "javascript:alert(1)", "file:///C:/secret", "magnet:?xt=urn:btih:short", "magnet:?xt=urn:btmh:1220bad", "magnet:?dn=NoTopic", "magnet:?xt=urn:btih:" + Hash + "\r\nInjected" })
            Require(!ReleaseSource.TryParseMagnet(invalid, out _, out _), "Only a valid torrent magnet may start a download.");
        Require(ReleaseSource.TryTorrentUri("/download/42.torrent", Feed, false, out var torrent) && torrent!.AbsoluteUri == "https://example.test/download/42.torrent", "Relative torrent links should resolve against the feed.");
        Require(!ReleaseSource.TryTorrentUri("https://example.test/details/42", Feed, false, out _) &&
            !ReleaseSource.TryTorrentUri("https://user:password@example.test/file.torrent", Feed, true, out _) &&
            !ReleaseSource.TryTorrentUri("file:///C:/arbitrary.torrent", Feed, true, out _), "HTML pages, embedded credentials and filesystem schemes must not become feed downloads.");
        Require(ReleaseSource.TryTorrentUri("https://example.test/get?id=42", Feed, true, out _), "A declared BitTorrent enclosure may use a download endpoint without an extension.");
    }

    private static void CheckParse()
    {
        string rss = """
            <rss version="2.0" xmlns:nyaa="https://nyaa.test/xmlns/nyaa"><channel>
             <item><title>Example Anime - 01 [1080p]</title><link>https://example.test/view/1</link><enclosure url="/get?id=1" type="application/x-bittorrent" length="123456"/><pubDate>Wed, 18 Nov 2026 18:00:00 GMT</pubDate></item>
             <item><title>Other Anime - 02 [720p]</title><link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=Other</link></item>
             <item><title>Duplicate with different display name</title><link>magnet:?dn=Changed&amp;xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567</link></item>
             <item><title>Hash-only release</title><link>https://example.test/view/3</link><nyaa:infoHash>2222222222222222222222222222222222222222</nyaa:infoHash></item>
             <item><title>Unsafe launch</title><enclosure url="file:///C:/arbitrary.exe" type="application/x-bittorrent"/></item>
             <item><title>Details only</title><link>https://example.test/view/5</link></item>
            </channel></rss>
            """;
        var entries = ReleaseFeedClient.Parse(Encoding.UTF8.GetBytes(rss), Feed);
        Require(entries.Count == 4 && entries[0].DownloadUri!.AbsolutePath == "/get" && entries[0].SizeBytes == 123456 && entries[0].Published?.Year == 2026,
            "RSS should expose torrent enclosures, dates and byte sizes while filtering unrelated links.");
        Require(entries[1].Identity == "urn:btih:" + Hash && entries[2].DownloadUri!.Scheme == "magnet" && entries[3].DownloadUri is null && entries[3].PageUri is not null,
            "RSS magnets must deduplicate by normalized identity, infoHash may supply a magnet, and details-only rows must remain explicitly unresolved.");
        string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title> Français 日本語 &amp; Friends </title><published>2026-11-18T20:00:00+01:00</published><link rel="alternate" href="https://example.test/show"/><link rel="enclosure" type="application/x-bittorrent" href="../episode.torrent" length="2345"/></entry></feed>
            """;
        var entry = ReleaseFeedClient.Parse(Encoding.UTF8.GetBytes(atom), Feed).Single();
        Require(entry.Title == "Français 日本語 & Friends" && entry.DownloadUri!.AbsolutePath == "/episode.torrent" && entry.SizeBytes == 2345 && entry.Published?.Offset == TimeSpan.FromHours(1), "Atom should retain plain Unicode text, timezone and enclosure metadata.");
        foreach (string bad in new[] { "not XML", "<html><body>Login needed</body></html>", "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/secret'>]><rss><channel>&x;</channel></rss>",
            "<rss><channel>" + string.Concat(Enumerable.Repeat("<nested>", 40)) + string.Concat(Enumerable.Repeat("</nested>", 40)) + "</channel></rss>" })
            Throws<InvalidDataException>(() => ReleaseFeedClient.Parse(Encoding.UTF8.GetBytes(bad), Feed));
        string many = "<rss><channel>" + string.Concat(Enumerable.Range(0, 250).Select(i => $"<item><title>Release {i}</title><link>https://example.test/{i}.torrent</link></item>")) + "</channel></rss>";
        Require(ReleaseFeedClient.Parse(Encoding.UTF8.GetBytes(many), Feed).Count == 200, "A large feed must be capped at 200 release rows.");
        Throws<InvalidDataException>(() => ReleaseFeedClient.Parse(new byte[ReleaseFeedClient.MaximumResponseBytes + 1], Feed));
    }

    private static async Task CheckFetch()
    {
        int calls = 0;
        using var handler = new Handler((request, token) => {
            calls++;
            Require(request.Method == HttpMethod.Get && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Feed requests must not carry account credentials or cookies.");
            return Task.FromResult(Xml("<rss><channel><item><title>Sample</title><link>https://example.test/1.torrent</link></item></channel></rss>"));
        });
        using var http = new HttpClient(handler);
        var feed = new ReleaseFeedClient(http);
        Require(calls == 0, "Constructing a feed client must not fetch or download anything.");
        Require((await feed.FetchAsync(Feed.AbsoluteUri, default)).Count == 1 && calls == 1, "Only an explicit refresh should load a feed.");
        await ThrowsAsync<ArgumentException>(() => feed.FetchAsync("file:///C:/private.xml", default));
        Require(calls == 1, "An invalid feed scheme must be rejected before network access.");
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            int count = 0;
            using var errorHandler = new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(status)); });
            using var errorHttp = new HttpClient(errorHandler);
            await ThrowsAsync<HttpRequestException>(() => new ReleaseFeedClient(errorHttp).FetchAsync(Feed.AbsoluteUri, default));
            Require(count == 1, "Redirects and server errors must not be replayed automatically.");
        }
        using var largeHandler = new Handler((_, _) => Task.FromResult(Xml(new string('x', ReleaseFeedClient.MaximumResponseBytes + 1))));
        using var largeHttp = new HttpClient(largeHandler);
        await ThrowsAsync<InvalidDataException>(() => new ReleaseFeedClient(largeHttp).FetchAsync(Feed.AbsoluteUri, default));
        using var delayHandler = new Handler(async (_, token) => { await Task.Delay(10000, token); return Xml("<rss/>"); });
        using var delayHttp = new HttpClient(delayHandler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await ThrowsAsync<OperationCanceledException>(() => new ReleaseFeedClient(delayHttp).FetchAsync(Feed.AbsoluteUri, cancel.Token));

        int pageCalls = 0;
        using var pageHandler = new Handler((request, _) => {
            pageCalls++;
            Require(request.RequestUri!.AbsoluteUri == "https://nyaa.media/view/123", "Only the selected supported Nyaa details page may be resolved.");
            Require(request.Headers.Accept.Any(x => x.MediaType == "text/html"), "Release-page resolution should request HTML metadata explicitly.");
            return Task.FromResult(Xml("<html><a href='javascript:alert(1)'>bad</a><a href=\"magnet:?xt=urn:btih:" + Hash + "&amp;dn=Example\">download</a></html>"));
        });
        using var pageHttp = new HttpClient(pageHandler);
        var resolver = new ReleaseFeedClient(pageHttp);
        var unresolved = new ReleaseFeedEntry("Example", null, null, null, "page:123") { PageUri = new("https://nyaa.media/view/123") };
        Require(pageCalls == 0 && (await resolver.ResolveNyaaAsync(unresolved, default)).Scheme == "magnet" && pageCalls == 1,
            "A details-only row should resolve its selected page only after an explicit request, with HTML-decoded validated links.");
        foreach (string unsafePage in new[] { "http://nyaa.media/view/123", "https://attacker.test/view/123", "https://nyaa.media/account", "https://nyaa.media/view/123?redirect=other", "https://nyaa.media:444/view/123" })
            await ThrowsAsync<InvalidOperationException>(() => resolver.ResolveNyaaAsync(unresolved with { PageUri = new(unsafePage) }, default));
        Require(pageCalls == 1, "Unsupported pages must be rejected before network access.");
        using var noLinkHandler = new Handler((_, _) => Task.FromResult(Xml("<html><a href='file:///C:/secret'>unsafe</a><script>not executed</script></html>")));
        using var noLinkHttp = new HttpClient(noLinkHandler);
        await ThrowsAsync<InvalidDataException>(() => new ReleaseFeedClient(noLinkHttp).ResolveNyaaAsync(unresolved, default));
        using var ignoredLinkHandler = new Handler((_, _) => Task.FromResult(Xml(
            "<html><!-- <a href='magnet:?xt=urn:btih:" + Hash + "'>comment</a> --><script><a href='magnet:?xt=urn:btih:" + Hash + "'>script</a></script><a data-href='magnet:?xt=urn:btih:" + Hash + "'>not a link</a></html>")));
        using var ignoredLinkHttp = new HttpClient(ignoredLinkHandler);
        await ThrowsAsync<InvalidDataException>(() => new ReleaseFeedClient(ignoredLinkHttp).ResolveNyaaAsync(unresolved, default));
        using var pageSizeHandler = new Handler((_, _) => Task.FromResult(Xml(new string('x', 1024 * 1024 + 1))));
        using var pageSizeHttp = new HttpClient(pageSizeHandler);
        await ThrowsAsync<InvalidDataException>(() => new ReleaseFeedClient(pageSizeHttp).ResolveNyaaAsync(unresolved, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    private static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/rss+xml") };
    private static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
