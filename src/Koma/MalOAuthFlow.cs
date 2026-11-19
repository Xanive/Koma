using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Koma;

/// <summary>A short-lived loopback listener. MyAnimeList supports PKCE with the plain method.</summary>
internal sealed class MalOAuthFlow
{
    internal const string RedirectUri = "http://127.0.0.1:43857/callback";
    private const int Port = 43857;
    internal string Verifier { get; } = RandomValue();
    private readonly string state = RandomValue();
    private bool used;

    internal Uri AuthorizationUri(string clientId)
    {
        if (!Regex.IsMatch(clientId, @"\A[a-zA-Z0-9._-]{16,256}\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Save your MyAnimeList Client ID first.");
        return new Uri("https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id=" +
            Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&state=" + state + "&code_challenge=" + Verifier + "&code_challenge_method=plain");
    }

    public async Task<string> AuthorizeAsync(string clientId, Action<Uri> openBrowser, CancellationToken token)
    {
        if (used) throw new InvalidOperationException("Start a new MyAnimeList connection.");
        used = true;
        var uri = AuthorizationUri(clientId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(4); }
            catch (SocketException) { throw new InvalidOperationException("The MyAnimeList callback port is in use. Close another connection attempt and try again."); }
            try { openBrowser(uri); }
            catch { throw new InvalidOperationException("Your browser could not open. Try connecting again."); }
            while (true)
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                socket.NoDelay = true;
                await using var stream = socket.GetStream();
                using var requestLimit = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestLimit.CancelAfter(TimeSpan.FromSeconds(5));
                string? code = null;
                bool denied = false;
                bool valid = false;
                try
                {
                    string request = await ReadHeadersAsync(stream, requestLimit.Token);
                    valid = TryReadCallback(request, out code, out denied);
                    await ReplyAsync(stream, valid, requestLimit.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                { deadline.Token.ThrowIfCancellationRequested(); }
                if (!valid) continue;
                if (denied) throw new InvalidOperationException("MyAnimeList access was not approved. Your account remains disconnected.");
                return code!;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("MyAnimeList sign-in timed out. Try connecting again."); }
        finally { listener.Stop(); }
    }

    // Kept independent of sockets so malformed, duplicate and cross-session replies can be tested.
    internal bool TryReadCallback(string headers, out string? code, out bool denied)
    {
        code = null; denied = false;
        string[] lines = headers.Split("\r\n", StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] is not ("HTTP/1.1" or "HTTP/1.0") ||
            !first[1].StartsWith("/callback?", StringComparison.Ordinal) || first[1].Length > 6000) return false;
        string[] hosts = lines.Skip(1).Where(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1 || !hosts[0][5..].Trim().Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(RedirectUri + first[1]["/callback".Length..], UriKind.Absolute, out var uri)) return false;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string item in uri.Query.TrimStart('?').Split('&'))
            {
                string[] pair = item.Split('=', 2);
                string key = Uri.UnescapeDataString(pair[0]);
                string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
                if (!query.TryAdd(key, value)) return false;
            }
        }
        catch (UriFormatException) { return false; }
        if (!query.TryGetValue("state", out var received) || received.Length != state.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(received), Encoding.ASCII.GetBytes(state))) return false;
        if (query.TryGetValue("error", out _)) { denied = true; return true; }
        if (!query.TryGetValue("code", out string? receivedCode) || receivedCode.Length is < 1 or > 4096 ||
            receivedCode.Any(c => c < '!' || c > '~')) return false;
        code = receivedCode;
        return true;
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(length, 1), token);
            if (count == 0) throw new IOException();
            length += count;
            if (length >= 4 && buffer[length - 4] == 13 && buffer[length - 3] == 10 &&
                buffer[length - 2] == 13 && buffer[length - 1] == 10)
                return Encoding.ASCII.GetString(buffer, 0, length);
        }
        throw new IOException();
    }

    private static async Task ReplyAsync(NetworkStream stream, bool accepted, CancellationToken token)
    {
        string text = accepted ? "Koma received the MyAnimeList reply. You can close this tab and return to Koma." :
            "This reply did not match Koma's sign-in attempt. Return to Koma and try again.";
        string body = "<!doctype html><meta charset=\"utf-8\"><title>Koma</title><h1>Koma</h1><p>" + text + "</p>";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string header = "HTTP/1.1 " + (accepted ? "200 OK" : "400 Bad Request") +
            "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length +
            "\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'" +
            "\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        await stream.WriteAsync(bytes, token);
    }

    private static string RandomValue() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
