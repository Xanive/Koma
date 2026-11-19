using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Koma;

internal sealed record MalAccountProfile(int Id, string Name);
internal sealed record MalListStatus(string Status, int WatchedEpisodes, bool IsRewatching,
    DateTimeOffset? UpdatedAt, int TotalEpisodes = 0);
internal sealed record MalListEntry(int Id, string Title, int TotalEpisodes, MalListStatus Status)
{
    public MalAnimeCandidate? Candidate { get; init; }
}
internal sealed record MalListPage(IReadOnlyList<MalListEntry> Entries, int? NextOffset);

/// <summary>Account endpoints are fixed: server-supplied paging links are never followed.</summary>
internal sealed class MalAccountClient(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken)
{
    private const string Api = "https://api.myanimelist.net/v2/";
    private const int PageSize = 100;
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) {
        "watching", "completed", "on_hold", "dropped", "plan_to_watch"
    };

    public async Task<MalAccountProfile> GetProfileAsync(CancellationToken token)
    {
        using var data = await SendAsync(HttpMethod.Get, "users/@me", null, token);
        var root = data.RootElement;
        return new(Positive(root, "id"), Text(root, "name", 100));
    }

    public async Task<MalListPage> GetListAsync(string? status, int offset, CancellationToken token)
    {
        if (status is not null && !Statuses.Contains(status)) throw new ArgumentException("Unknown list filter.");
        if (offset is < 0 or > 1000000) throw new ArgumentOutOfRangeException(nameof(offset));
        string fields = MalCandidateMetadata.RequestedFields.Replace(",synopsis", "", StringComparison.Ordinal);
        string path = $"users/@me/animelist?fields=list_status,{fields}&sort=list_updated_at&limit={PageSize}&offset={offset}";
        if (status is not null) path += "&status=" + status;
        using var document = await SendAsync(HttpMethod.Get, path, null, token);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() > PageSize) throw InvalidData();
        var entries = new List<MalListEntry>();
        var ids = new HashSet<int>();
        bool metadataIncomplete = false;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("node", out var node) || !item.TryGetProperty("list_status", out var listStatus))
                throw InvalidData();
            int id = Positive(node, "id");
            int total = Number(node, "num_episodes", 0);
            if (!ids.Add(id)) { metadataIncomplete = true; continue; }
            var candidate = MalCandidateMetadata.Read(node, out bool incomplete);
            metadataIncomplete |= incomplete;
            entries.Add(new(id, Text(node, "title", 300), total, ParseStatus(listStatus, total)) { Candidate = candidate });
        }
        if (metadataIncomplete)
            for (int index = 0; index < entries.Count; index++)
                if (entries[index].Candidate is { } candidate)
                    entries[index] = entries[index] with { Candidate = candidate with { SearchIncomplete = true } };
        bool more = root.TryGetProperty("paging", out var paging) && paging.ValueKind == JsonValueKind.Object &&
            paging.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(next.GetString());
        // Derive the next offset ourselves. An empty page cannot advance the cursor.
        int? nextOffset = more && data.GetArrayLength() > 0 && offset + data.GetArrayLength() <= 1000000
            ? offset + data.GetArrayLength() : null;
        return new(entries, nextOffset);
    }

    public async Task<MalListStatus?> GetStatusAsync(int animeId, CancellationToken token)
        => (await GetDetailsAsync(animeId, token)).Status;

    private async Task<(MalListStatus? Status, int Total)> GetDetailsAsync(int animeId, CancellationToken token)
    {
        CheckId(animeId);
        using var document = await SendAsync(HttpMethod.Get,
            $"anime/{animeId}?fields=my_list_status,num_episodes", null, token);
        var root = document.RootElement;
        int total = Number(root, "num_episodes", 0);
        if (!root.TryGetProperty("my_list_status", out var status) || status.ValueKind == JsonValueKind.Null)
            return (null, total);
        return (ParseStatus(status, total), total);
    }

    public async Task<MalListStatus> UpdateProgressAsync(int animeId, int watched, int expectedPrevious,
        CancellationToken token, bool? expectedExists = null)
    {
        CheckId(animeId);
        if (watched is <= 0 or > 100000 || expectedPrevious is < 0 or > 100000 || watched <= expectedPrevious)
            throw new ArgumentOutOfRangeException(nameof(watched));
        // Reconcile immediately before writing. No retries follow an ambiguous write failure.
        var details = await GetDetailsAsync(animeId, token);
        var current = details.Status;
        token.ThrowIfCancellationRequested();
        if (expectedExists.HasValue && expectedExists.Value != (current is not null) ||
            (current?.WatchedEpisodes ?? 0) != expectedPrevious ||
            current is { IsRewatching: true } || current?.Status is "on_hold" or "dropped" or "completed")
            throw new InvalidOperationException("MyAnimeList progress changed or needs review. Refresh before updating.");
        if (details.Total > 0 && watched > details.Total)
            throw new InvalidOperationException("The episode number exceeds this MyAnimeList entry. Review the match.");
        var fields = new Dictionary<string, string> { ["num_watched_episodes"] = watched.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        string nextStatus = details.Total > 0 && watched == details.Total ? "completed" : "watching";
        if (current?.Status != nextStatus) fields["status"] = nextStatus;
        token.ThrowIfCancellationRequested();
        using var document = await SendAsync(HttpMethod.Patch, $"anime/{animeId}/my_list_status", fields, token);
        var result = ParseStatus(document.RootElement, details.Total);
        if (result.WatchedEpisodes < watched) throw new InvalidOperationException("MyAnimeList did not confirm this progress. Refresh to review.");
        return result;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, Dictionary<string, string>? fields,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); token = deadline.Token;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string bearer = await accessToken(attempt > 0, token);
            using var request = new HttpRequestMessage(method, Api + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("Koma/0.2");
            if (fields is not null) request.Content = new FormUrlEncodedContent(fields);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            // A rejected token has not applied a PATCH. Other failures are never replayed.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode switch {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Reconnect your MyAnimeList account.",
                HttpStatusCode.TooManyRequests => "MyAnimeList is busy. Wait before syncing again.",
                _ => "MyAnimeList could not complete this request."
            }, null, response.StatusCode);
            return await ReadJsonAsync(response.Content, token);
        }
        throw new HttpRequestException("Reconnect your MyAnimeList account.");
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken token)
    {
        const int maximum = 1024 * 1024;
        if (content.Headers.ContentLength > maximum) throw InvalidData();
        await using var source = await content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (bytes.Length + count > maximum) throw InvalidData();
            bytes.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException) { throw InvalidData(); }
    }

    private static MalListStatus ParseStatus(JsonElement value, int total)
    {
        string status = Text(value, "status", 32);
        if (!Statuses.Contains(status)) throw InvalidData();
        int watched = Number(value, "num_episodes_watched");
        bool rewatch = value.TryGetProperty("is_rewatching", out var flag) && flag.ValueKind == JsonValueKind.True;
        DateTimeOffset? updated = value.TryGetProperty("updated_at", out var at) && at.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
        return new(status, watched, rewatch, updated, total);
    }

    private static int Positive(JsonElement value, string key)
    { int number = Number(value, key); return number > 0 ? number : throw InvalidData(); }
    private static int Number(JsonElement value, string key, int? fallback = null)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int number) && number is >= 0 and <= 100000000)
            return number;
        return fallback ?? throw InvalidData();
    }
    private static string Text(JsonElement value, string key, int maximum)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl)) return text;
        throw InvalidData();
    }
    private static void CheckId(int id) { if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id)); }
    private static HttpRequestException InvalidData() => new("MyAnimeList returned an unreadable response.");
}
