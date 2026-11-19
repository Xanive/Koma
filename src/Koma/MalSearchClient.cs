using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Koma;

/// <summary>Read-only catalog search. The caller owns the HTTP client and must disable redirects.</summary>
internal sealed class MalSearchClient
{
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly HttpClient http;
    private readonly string clientId;

    public MalSearchClient(HttpClient http, string clientId)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 256 ||
            !Regex.IsMatch(clientId, @"\A[a-zA-Z0-9._-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A valid MyAnimeList client ID is required.", nameof(clientId));
        this.http = http;
        this.clientId = clientId;
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler {
        AllowAutoRedirect = false, MaxResponseHeadersLength = 16,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<IReadOnlyList<MalAnimeCandidate>> SearchAsync(string title, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); token = deadline.Token;
        title = ValidateTitle(title);
        string uri = "https://api.myanimelist.net/v2/anime?q=" + Uri.EscapeDataString(title) +
            "&limit=10&nsfw=false&fields=" + MalCandidateMetadata.RequestedFields;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-MAL-CLIENT-ID", clientId);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Koma/0.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode switch {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "MyAnimeList did not accept this app's registration.",
                HttpStatusCode.TooManyRequests => "MyAnimeList is busy. Wait before searching again.",
                _ => "MyAnimeList could not complete this search."
            };
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new HttpRequestException("MyAnimeList returned too much data.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (output.Length + count > MaximumResponseBytes)
                throw new HttpRequestException("MyAnimeList returned too much data.");
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        try { return Parse(output.ToArray()); }
        catch (JsonException) { throw new HttpRequestException("MyAnimeList returned an unreadable response."); }
    }

    private static string ValidateTitle(string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl) ||
            title.Contains('\\') || title.StartsWith('/') || title.Contains("://", StringComparison.Ordinal) ||
            Regex.IsMatch(title, @"\A[a-z]:|(?:\A|/)\.\.(?:/|\z)|\.(?:mkv|mp4|avi|webm|m4v|srt|ass)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            title.Count(x => x == '/') > 1)
            throw new ArgumentException("Search with an anime title, not a file path.", nameof(title));
        return title;
    }

    private static IReadOnlyList<MalAnimeCandidate> Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        var matches = new List<MalAnimeCandidate>();
        var ids = new HashSet<int>();
        bool incomplete = data.GetArrayLength() > 10 ||
            document.RootElement.TryGetProperty("paging", out var paging) && paging.ValueKind == JsonValueKind.Object &&
            paging.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(next.GetString());
        foreach (var entry in data.EnumerateArray().Take(10))
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("node", out var node))
            { incomplete = true; continue; }
            var candidate = MalCandidateMetadata.Read(node, out bool partial);
            incomplete |= partial;
            if (candidate is null || !ids.Add(candidate.Id)) { incomplete = true; continue; }
            matches.Add(candidate);
        }
        return matches.Select(x => x with { SearchIncomplete = incomplete }).ToArray();
    }

}
