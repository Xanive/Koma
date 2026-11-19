using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Koma;

internal sealed record ReleaseFeedEntry(string Title, Uri? DownloadUri, DateTimeOffset? Published,
    long? SizeBytes, string Identity)
{
    public Uri? PageUri { get; init; }
    public int? Seeders { get; init; }
}

/// <summary>Accepts torrent sources, never arbitrary operating-system URI handlers.</summary>
internal static class ReleaseSource
{
    public static bool TryParseMagnet(string? text, out Uri? magnet, out string identity)
    {
        magnet = null; identity = "";
        if (text is not { Length: >= 8 and <= 16384 } || !text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
            text.Any(char.IsControl) || text.Contains('#')) return false;
        var fields = new List<(string Name, string Value)>();
        string[] parts = text[8..].Split('&');
        if (parts.Length > 64) return false;
        try
        {
            foreach (string part in parts)
            {
                string[] pair = part.Split('=', 2);
                if (pair.Length != 2) continue;
                string name = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
                string value = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                if (!value.Any(char.IsControl)) fields.Add((name, value));
            }
        }
        catch (UriFormatException) { return false; }

        var topics = fields.Where(x => x.Name == "xt").Select(x => NormalizeTopic(x.Value))
            .Where(x => x is not null).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (topics.Length == 0 || topics.Length > 2 ||
            topics.Count(x => x.StartsWith("urn:btih:", StringComparison.Ordinal)) > 1 ||
            topics.Count(x => x.StartsWith("urn:btmh:", StringComparison.Ordinal)) > 1) return false;
        identity = topics.FirstOrDefault(x => x.StartsWith("urn:btih:", StringComparison.Ordinal)) ?? topics[0];
        var query = topics.Select(topic => "xt=" + Uri.EscapeDataString(topic)).ToList();
        string? nameValue = fields.FirstOrDefault(x => x.Name == "dn").Value;
        if (!string.IsNullOrWhiteSpace(nameValue) && nameValue.Length <= 300) query.Add("dn=" + Uri.EscapeDataString(nameValue));
        foreach (string tracker in fields.Where(x => x.Name == "tr").Select(x => x.Value).Distinct(StringComparer.Ordinal).Take(20))
            if (TryWebUri(tracker, null, out var uri, allowUdp: true)) query.Add("tr=" + Uri.EscapeDataString(uri!.AbsoluteUri));
        // Webseed and metadata-source parameters are deliberately excluded from feed-origin magnets.
        return Uri.TryCreate("magnet:?" + string.Join('&', query), UriKind.Absolute, out magnet);
    }

    internal static bool TryTorrentUri(string? text, Uri? baseUri, bool declaredTorrent, out Uri? uri)
    {
        if (TryParseMagnet(text, out uri, out _)) return true;
        if (!TryWebUri(text, baseUri, out uri)) return false;
        if (declaredTorrent || uri!.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return true;
        uri = null; return false;
    }

    internal static bool TryWebUri(string? text, Uri? baseUri, out Uri? uri, bool allowUdp = false)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192 || text.Any(char.IsControl)) return false;
        if (!(Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) || baseUri is not null && Uri.TryCreate(baseUri, text.Trim(), out uri))) return false;
        return uri is { IsAbsoluteUri: true } && (uri.Scheme is "http" or "https" || allowUdp && uri.Scheme == "udp") &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }

    private static string? NormalizeTopic(string value)
    {
        if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
        {
            string hash = value[9..];
            if (hash.Length == 40 && hash.All(Uri.IsHexDigit)) return "urn:btih:" + hash.ToLowerInvariant();
            if (hash.Length == 32 && hash.All(c => char.ToUpperInvariant(c) is >= 'A' and <= 'Z' or >= '2' and <= '7'))
                return "urn:btih:" + Base32Hex(hash);
        }
        if (value.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase) && value.Length == 77 && value[13..].All(Uri.IsHexDigit))
            return value.ToLowerInvariant();
        return null;
    }

    private static string Base32Hex(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); int buffer = 0, bits = 0;
        foreach (char digit in value.ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(digit); bits += 5;
            if (bits < 8) continue;
            bits -= 8; bytes.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1;
        }
        return Convert.ToHexString([.. bytes]).ToLowerInvariant();
    }
}

/// <summary>Only an explicit refresh fetches a configured feed; this class never starts downloads.</summary>
internal sealed class ReleaseFeedClient(HttpClient http)
{
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumItems = 200;
    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(100);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 2, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<IReadOnlyList<ReleaseFeedEntry>> FetchAsync(string url, CancellationToken token)
    {
        if (!ReleaseSource.TryWebUri(url, null, out var source)) throw new ArgumentException("Enter an HTTP or HTTPS release-feed address.");
        byte[] content = await FetchBytesAsync(source!, MaximumResponseBytes, token);
        return Parse(content, source!);
    }

    public async Task<Uri> ResolveNyaaAsync(ReleaseFeedEntry release, CancellationToken token)
    {
        if (release.DownloadUri is not null) return release.DownloadUri;
        if (!IsNyaaPage(release.PageUri)) throw new InvalidOperationException("This release feed does not provide a supported torrent link.");
        byte[] bytes = await FetchBytesAsync(release.PageUri!, 1024 * 1024, token, page: true);
        string html = System.Text.Encoding.UTF8.GetString(bytes);
        // This reads quoted link attributes only. HTML and scripts are never executed or displayed.
        Match[] matches;
        try
        {
            html = Regex.Replace(html, @"<!--.*?-->|<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>", "",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexLimit);
            matches = Regex.Matches(html, @"<a\b[^<>]{0,2048}?(?<![-\w:])href\s*=\s*(?:""(?<uri>[^""<>]{1,16384})""|'(?<uri>[^'<>]{1,16384})')",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit).Cast<Match>().ToArray();
        }
        catch (RegexMatchTimeoutException) { throw new InvalidDataException("The selected release page could not be read safely."); }
        foreach (Match match in matches)
            if (ReleaseSource.TryParseMagnet(WebUtility.HtmlDecode(match.Groups["uri"].Value), out var magnet, out _)) return magnet!;
        foreach (Match match in matches)
            if (ReleaseSource.TryTorrentUri(WebUtility.HtmlDecode(match.Groups["uri"].Value), release.PageUri, false, out var torrent) &&
                torrent!.Scheme == "https" && torrent.Host.Equals("nyaa.media", StringComparison.OrdinalIgnoreCase) && torrent.Port == 443) return torrent;
        throw new InvalidDataException("The selected release page did not provide a torrent link.");
    }

    private async Task<byte[]> FetchBytesAsync(Uri source, int maximum, CancellationToken token, bool page = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.ParseAdd(page ? "text/html, application/xhtml+xml" : "application/rss+xml, application/atom+xml, application/xml, text/xml");
        request.Headers.UserAgent.ParseAdd("Koma/0.2");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The release feed is unavailable. Check its address or try again later.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The release response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int length;
            while ((length = await stream.ReadAsync(chunk.AsMemory(), token)) > 0)
            {
                if (buffer.Length + length > maximum) throw new InvalidDataException("The release response is too large.");
                buffer.Write(chunk, 0, length);
            }
            token.ThrowIfCancellationRequested();
            return buffer.ToArray();
        }
        catch (HttpRequestException error)
        { throw new HttpRequestException("The release feed is unavailable. Check its address or try again later.", null, error.StatusCode); }
    }

    internal static IReadOnlyList<ReleaseFeedEntry> Parse(byte[] xml, Uri feedUri)
    {
        if (xml.Length > MaximumResponseBytes) throw new InvalidDataException("The release feed is too large.");
        try
        {
            using var input = new MemoryStream(xml, writable: false);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumResponseBytes, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true };
            // Check nesting before materializing the bounded document into a tree.
            using (var validator = XmlReader.Create(input, settings))
                while (validator.Read()) if (validator.Depth > 32) throw InvalidFeed();
            input.Position = 0;
            using var reader = XmlReader.Create(input, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root is not { } root) throw InvalidFeed();
            IEnumerable<XElement> items = root.Name.LocalName switch {
                "rss" => root.Elements().Where(x => x.Name.LocalName == "channel").SelectMany(x => x.Elements()).Where(x => x.Name.LocalName == "item"),
                "feed" => root.Elements().Where(x => x.Name.LocalName == "entry"),
                "RDF" => root.Elements().Where(x => x.Name.LocalName == "item"),
                _ => throw InvalidFeed()
            };
            var results = new List<ReleaseFeedEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.Take(MaximumItems))
            {
                string? title = PlainText(item.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value, 300);
                if (title is null) continue;
                var candidates = new List<(string? Link, bool Torrent, long? Size)>();
                foreach (var child in item.Elements())
                {
                    string kind = child.Name.LocalName;
                    if (kind == "enclosure") candidates.Add(((string?)child.Attribute("url"), TorrentMime((string?)child.Attribute("type")), Size((string?)child.Attribute("length"))));
                    else if (kind == "link")
                        candidates.Add(((string?)child.Attribute("href") ?? child.Value.Trim(),
                            (string?)child.Attribute("rel") == "enclosure" && TorrentMime((string?)child.Attribute("type")), Size((string?)child.Attribute("length"))));
                    else if (kind is "magnetURI" or "magnetUri" or "magnet" or "guid") candidates.Add((child.Value.Trim(), false, null));
                }
                Uri? download = null; Uri? page = null; string identity = ""; long? size = null;
                foreach (var candidate in candidates)
                    if (ReleaseSource.TryWebUri(candidate.Link, feedUri, out var web) && !candidate.Torrent &&
                        !web!.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) { page = web; break; }
                foreach (var candidate in candidates)
                    if (ReleaseSource.TryParseMagnet(candidate.Link, out download, out identity)) { size = candidate.Size; break; }
                if (download is null)
                {
                    string? infoHash = item.Elements().FirstOrDefault(x => x.Name.LocalName == "infoHash")?.Value.Trim();
                    if (infoHash is { Length: 40 } && infoHash.All(Uri.IsHexDigit))
                        ReleaseSource.TryParseMagnet("magnet:?xt=urn:btih:" + infoHash + "&dn=" + Uri.EscapeDataString(title), out download, out identity);
                }
                if (download is null)
                    foreach (var candidate in candidates)
                        if (ReleaseSource.TryTorrentUri(candidate.Link, feedUri, candidate.Torrent, out var uri))
                        { download = uri; identity = uri!.AbsoluteUri; size = candidate.Size; break; }
                if (download is null && page is null) continue;
                if (download is null) identity = "page:" + page!.AbsoluteUri;
                if (!seen.Add(identity)) continue;
                size ??= DisplaySize(item.Elements().FirstOrDefault(x => x.Name.LocalName == "size")?.Value);
                string? stamp = item.Elements().FirstOrDefault(x => x.Name.LocalName is "pubDate" or "published" or "updated" or "date")?.Value;
                DateTimeOffset? published = DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date) ? date : null;
                results.Add(new(title, download, published, size, identity) { PageUri = page,
                    Seeders = int.TryParse(item.Elements().FirstOrDefault(x => x.Name.LocalName == "seeders")?.Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out int seeders) && seeders >= 0 ? seeders : null });
            }
            return results;
        }
        catch (Exception error) when (error is XmlException or ArgumentException or RegexMatchTimeoutException)
        { throw InvalidFeed(); }
    }

    private static bool TorrentMime(string? value) => value?.Split(';')[0].Trim().Equals("application/x-bittorrent", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsNyaaPage(Uri? uri) => uri is { Scheme: "https", Host: "nyaa.media", Port: 443, UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 } &&
        Regex.IsMatch(uri.AbsolutePath, @"\A/view/[0-9]{1,12}\z", RegexOptions.CultureInvariant, RegexLimit);
    private static long? Size(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long size) && size >= 0 && size <= (1L << 50) ? size : null;
    private static long? DisplaySize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40) return null;
        var match = Regex.Match(value, @"\A\s*(?<number>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>B|KiB|MiB|GiB|TiB|KB|MB|GB|TB)\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        if (!match.Success || !double.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number)) return null;
        double multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch {
            "KIB" => 1024d, "MIB" => 1024d * 1024, "GIB" => 1024d * 1024 * 1024, "TIB" => 1024d * 1024 * 1024 * 1024,
            "KB" => 1000d, "MB" => 1000000d, "GB" => 1000000000d, "TB" => 1000000000000d, _ => 1d
        };
        double bytes = number * multiplier;
        return double.IsFinite(bytes) && bytes >= 0 && bytes <= (1L << 50) ? (long)Math.Round(bytes) : null;
    }
    private static string? PlainText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) return null;
        string text = Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant, RegexLimit).Trim();
        return text.Length is > 0 && text.Length <= maximum ? text : null;
    }
    private static InvalidDataException InvalidFeed() => new("The address did not return a supported RSS or Atom release feed.");
}
