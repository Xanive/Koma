using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Koma;

/// <summary>Small, transient catalogue covers. No credentials, disk cache or arbitrary image hosts.</summary>
internal sealed class MatchCoverCache : IDisposable
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int MaximumEntries = 64;
    private readonly HttpClient http;
    private readonly SemaphoreSlim workers = new(2, 2);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Dictionary<string, CachedCover> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recent = new();
    private bool disposed;

    private sealed record CachedCover(BitmapSource? Image, DateTimeOffset RetryAfter, LinkedListNode<string> Node);

    // Handler injection is for canned-response tests; this instance owns its handler and client.
    internal MatchCoverCache(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 2, ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<BitmapSource?> LoadAsync(string? url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryCoverUri(url, out var uri)) return null;
        string key = uri!.AbsoluteUri;
        CancellationTokenSource request;
        lock (sync)
        {
            if (disposed) return null;
            if (TryCached(key, out var image)) return image;
            request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        }
        using (request)
        {
            request.CancelAfter(TimeSpan.FromSeconds(8));
            bool entered = false;
            try
            {
                await workers.WaitAsync(request.Token).ConfigureAwait(false);
                entered = true;
                lock (sync)
                {
                    if (disposed) return null;
                    if (TryCached(key, out var cached)) return cached;
                }
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                message.Headers.Accept.ParseAdd("image/jpeg, image/png");
                message.Headers.UserAgent.ParseAdd("Koma/0.2");
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                    return Remember(key, null);
                string? media = response.Content.Headers.ContentType?.MediaType;
                if (media is not null && media is not ("image/jpeg" or "image/png" or "application/octet-stream"))
                    return Remember(key, null);
                await using var input = await response.Content.ReadAsStreamAsync(request.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var bytes = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(bytes.AsMemory(), request.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > MaximumBytes) return Remember(key, null);
                    output.Write(bytes, 0, count);
                }
                request.Token.ThrowIfCancellationRequested();
                byte[] content = output.ToArray();
                var bitmap = await Task.Run(() => Decode(content), request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                return Remember(key, bitmap);
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or FileFormatException or ArgumentException or
                InvalidOperationException or NotSupportedException or COMException)
            { return Remember(key, null); }
            finally { if (entered) workers.Release(); }
        }
    }

    internal static bool TryCoverUri(string? value, out Uri? uri)
    {
        uri = null;
        if (value is not { Length: > 0 and <= 2048 } || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" ||
            parsed.Port != 443 || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0 ||
            parsed.Host is not ("cdn.myanimelist.net" or "images.myanimelist.net")) return false;
        string path = parsed.AbsolutePath;
        if (!path.Contains("/images/anime/", StringComparison.OrdinalIgnoreCase) ||
            !(path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) return false;
        uri = parsed;
        return true;
    }

    private bool TryCached(string key, out BitmapSource? image)
    {
        image = null;
        if (!cache.TryGetValue(key, out var saved)) return false;
        if (saved.Image is null && saved.RetryAfter <= DateTimeOffset.UtcNow)
        { recent.Remove(saved.Node); cache.Remove(key); return false; }
        recent.Remove(saved.Node); recent.AddLast(saved.Node);
        image = saved.Image;
        return true;
    }

    private BitmapSource? Remember(string key, BitmapSource? image)
    {
        lock (sync)
        {
            if (disposed) return null;
            if (cache.Remove(key, out var previous)) recent.Remove(previous.Node);
            var node = recent.AddLast(key);
            cache[key] = new(image, DateTimeOffset.UtcNow.AddSeconds(30), node);
            while (cache.Count > MaximumEntries && recent.First is { } oldest)
            { cache.Remove(oldest.Value); recent.RemoveFirst(); }
            return image;
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        bool png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpeg) return null;
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) return null;
        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 24_000_000) return null;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, Math.Min(width, Math.Min(100, 400 * width / height)));
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            cache.Clear(); recent.Clear();
        }
        lifetime.Cancel(); http.Dispose(); lifetime.Dispose();
        // Waiters retain their linked token; the semaphore is left for GC so active finally blocks can release it.
    }
}
