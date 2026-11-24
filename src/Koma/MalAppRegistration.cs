using System.IO;
using System.Text;
using System.Text.Json;

namespace Koma;

/// <summary>The public native-app identifier supplied by a Koma build, never user credentials.</summary>
internal sealed record MalAppRegistration(string ClientId)
{
    internal const string FileName = "Koma.MyAnimeList.json";
    private const int MaximumBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static MalAppRegistration? Read(string appDirectory)
    {
        try
        {
            string path = Path.Combine(appDirectory, FileName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumBytes) return null;
            byte[] bytes = new byte[MaximumBytes + 1];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            return count is > 0 and <= MaximumBytes ? Parse(Utf8.GetString(bytes, 0, count)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        { return null; }
    }

    internal static MalAppRegistration? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumBytes) return null;
        try
        {
            if (Utf8.GetByteCount(json) > MaximumBytes) return null;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("Version" or "ClientId" or "RedirectUri") || !fields.Add(property.Name)) return null;
            if (fields.Count != 3 || !root.GetProperty("Version").TryGetInt32(out int version) || version != 1 ||
                root.GetProperty("ClientId").ValueKind != JsonValueKind.String ||
                root.GetProperty("RedirectUri").ValueKind != JsonValueKind.String ||
                root.GetProperty("RedirectUri").GetString() != MalOAuthFlow.RedirectUri) return null;
            string? id = root.GetProperty("ClientId").GetString();
            return id is { Length: >= 16 and <= 256 } && id.All(c =>
                c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-') ? new(id) : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { return null; }
    }
}
