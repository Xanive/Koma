using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Koma;

/// <summary>OAuth material stays in a separate Windows-user-protected file.</summary>
internal sealed class MalAccountService : IDisposable
{
    private sealed record Tokens(string ClientFingerprint, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Koma.MyAnimeList.Account.v1");
    private readonly string tokenFile;
    private readonly Func<string?> clientId;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly MalAccountClient client;
    private readonly object sync = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private Tokens? saved;
    private CancellationTokenSource lifetime = new();
    private int generation;
    private int connecting;
    private bool disposed;

    public MalAccountService(string tokenFile, Func<string?> clientId, HttpClient? http = null)
    {
        this.tokenFile = tokenFile;
        this.clientId = clientId;
        this.http = http ?? MalSearchClient.CreateHttpClient();
        ownsHttp = http is null;
        saved = Read();
        client = new(this.http, GetAccessTokenAsync);
    }

    public bool IsConnected
    {
        get { lock (sync) return !disposed && saved is not null && MatchesClient(saved, clientId()); }
    }

    public async Task ConnectAsync(Action<Uri> openBrowser, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.CompareExchange(ref connecting, 1, 0) != 0)
            throw new InvalidOperationException("A MyAnimeList connection is already waiting for approval.");
        try
        {
            string id = clientId() ?? throw new InvalidOperationException("Save your MyAnimeList Client ID first.");
            int epoch;
            CancellationToken accountToken;
            lock (sync) { epoch = generation; accountToken = lifetime.Token; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, accountToken);
            var flow = new MalOAuthFlow();
            string code = await flow.AuthorizeAsync(id, openBrowser, linked.Token);
            var tokens = await RequestTokensAsync(new() {
                ["client_id"] = id, ["grant_type"] = "authorization_code", ["code"] = code,
                ["code_verifier"] = flow.Verifier, ["redirect_uri"] = MalOAuthFlow.RedirectUri
            }, id, linked.Token);
            lock (sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (disposed || epoch != generation || id != clientId()) throw new OperationCanceledException(linked.Token);
                Save(tokens);
                saved = tokens;
            }
        }
        finally { Interlocked.Exchange(ref connecting, 0); }
    }

    public void Disconnect()
    {
        CancellationTokenSource? previous = null;
        try
        {
            lock (sync)
            {
                generation++;
                saved = null;
                previous = lifetime;
                lifetime = new();
                if (File.Exists(tokenFile)) File.Delete(tokenFile);
            }
        }
        finally { previous?.Cancel(); previous?.Dispose(); }
    }

    public Task<MalAccountProfile> GetProfileAsync(CancellationToken token) => RunAsync(client.GetProfileAsync, token);
    public Task<MalListPage> GetListAsync(string? status, int offset, CancellationToken token) => RunAsync(t => client.GetListAsync(status, offset, t), token);
    public Task<MalListStatus?> GetStatusAsync(int animeId, CancellationToken token) => RunAsync(t => client.GetStatusAsync(animeId, t), token);
    public Task<MalListStatus> UpdateProgressAsync(int animeId, int watched, int expectedPrevious, CancellationToken token,
        bool? expectedExists = null) =>
        RunAsync(t => client.UpdateProgressAsync(animeId, watched, expectedPrevious, t, expectedExists), token);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        CancellationToken accountToken;
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); accountToken = lifetime.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, accountToken);
        return await operation(linked.Token);
    }

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await refreshGate.WaitAsync(token);
        try
        {
            Tokens current;
            string id;
            int epoch;
            CancellationToken accountToken;
            lock (sync)
            {
                id = clientId() ?? "";
                current = saved is not null && MatchesClient(saved, id) ? saved :
                    throw new InvalidOperationException("Connect your MyAnimeList account first.");
                epoch = generation; accountToken = lifetime.Token;
            }
            if (!forceRefresh && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return current.AccessToken;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, accountToken);
            var replacement = await RequestTokensAsync(new() {
                ["client_id"] = id, ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken
            }, id, linked.Token);
            lock (sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (disposed || epoch != generation || id != clientId()) throw new OperationCanceledException(linked.Token);
                Save(replacement); saved = replacement;
            }
            return replacement.AccessToken;
        }
        finally { refreshGate.Release(); }
    }

    private async Task<Tokens> RequestTokensAsync(Dictionary<string, string> fields, string id, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://myanimelist.net/v1/oauth2/token") {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode switch {
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "MyAnimeList sign-in expired or was rejected. Connect again.",
            HttpStatusCode.TooManyRequests => "MyAnimeList is busy. Wait before connecting again.",
            _ => "MyAnimeList could not finish signing in. Try again later."
        }, null, response.StatusCode);
        using var data = await MalAccountClient.ReadJsonAsync(response.Content, token);
        var root = data.RootElement;
        string access = ReadToken(root, "access_token");
        string refresh = ReadToken(root, "refresh_token");
        if (!root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "bearer", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out int seconds) || seconds is < 30 or > 31536000)
            throw new HttpRequestException("MyAnimeList returned an unreadable sign-in response.");
        return new(Fingerprint(id), access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private static string ReadToken(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: >= 1 and <= 16384 } text && !text.Any(c => c < '!' || c > '~')) return text;
        throw new HttpRequestException("MyAnimeList returned an unreadable sign-in response.");
    }

    private Tokens? Read()
    {
        try
        {
            if (!File.Exists(tokenFile) || new FileInfo(tokenFile).Length > 65536) return null;
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(tokenFile), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var value = JsonSerializer.Deserialize<Tokens>(clear);
                return value is not null && value.AccessToken is { Length: >= 1 and <= 16384 } && value.RefreshToken is { Length: >= 1 and <= 16384 } &&
                    !value.AccessToken.Any(c => c < '!' || c > '~') && !value.RefreshToken.Any(c => c < '!' || c > '~') &&
                    value.ClientFingerprint is { Length: 64 } ? value : null;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException)
        { return null; }
    }

    private void Save(Tokens value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(value);
        string scratch = tokenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(scratch, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
            File.Move(scratch, tokenFile, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool MatchesClient(Tokens value, string? id) => !string.IsNullOrEmpty(id) && value.ClientFingerprint == Fingerprint(id);

    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            if (disposed) return;
            disposed = true; generation++; saved = null; previous = lifetime;
        }
        previous.Cancel(); previous.Dispose();
        if (ownsHttp) http.Dispose();
    }
}
