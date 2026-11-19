using System.Globalization;
using System.IO;

namespace Koma;

/// <summary>A local choice, not a retained copy of the provider's catalogue data. Zero disables matching.</summary>
internal sealed record AnimeLink(int MalId, bool Confirmed, string LocalTitle, DateTimeOffset Updated);

internal static class AnimeLinkKeys
{
    private const int MaximumKeyLength = 4096;
    private const int MaximumTitleLength = 240;

    /// <summary>Empty means the local hints cannot supply a safe, stable mapping key.</summary>
    public static string For(Episode episode, DetectedAnime detection)
    {
        if (episode is null || detection is null || !ValidText(detection.Title, MaximumTitleLength) ||
            !Enum.IsDefined(detection.Kind) || detection.Season is < 0 or > 999)
            return "";
        string title = AnimeRecognition.NormalizeTitle(detection.Title);
        if (title.Length == 0) return "";

        string location;
        if (episode.FolderId is Guid id && id != Guid.Empty)
        {
            string? directory = RelativeDirectory(episode.Relative);
            if (directory is null) return "";
            location = $"folder:{id:N}|dir:{directory}";
        }
        else
        {
            if (!ValidText(episode.Path, MaximumKeyLength) || !Path.IsPathFullyQualified(episode.Path)) return "";
            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(episode.Path));
                if (directory is null || directory.Contains('|')) return "";
                location = "path:" + Path.TrimEndingDirectorySeparator(directory).Replace('\\', '/');
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                return "";
            }
        }

        string season = detection.Season?.ToString(CultureInfo.InvariantCulture) ?? "?";
        string key = $"{location}|title:{title}|season:{season}|kind:{detection.Kind.ToString().ToLowerInvariant()}";
        return ValidText(key, MaximumKeyLength) ? key : "";
    }

    public static Dictionary<string, AnimeLink> Normalize(Dictionary<string, AnimeLink>? links)
    {
        var normalized = new Dictionary<string, AnimeLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in links ?? [])
        {
            if (!ValidText(key, MaximumKeyLength) || value is not { MalId: >= 0 } ||
                !ValidText(value.LocalTitle, MaximumTitleLength) || AnimeRecognition.NormalizeTitle(value.LocalTitle).Length == 0)
                continue;
            var cleaned = value with { LocalTitle = value.LocalTitle.Trim() };
            if (!normalized.TryGetValue(key, out var previous) || cleaned.Updated > previous.Updated ||
                cleaned.Updated == previous.Updated && Prefer(cleaned, previous))
                normalized[key] = cleaned;
        }
        return normalized;
    }

    private static bool Prefer(AnimeLink candidate, AnimeLink previous) =>
        candidate.MalId == 0 && previous.MalId != 0 ||
        candidate.MalId != 0 && previous.MalId != 0 && candidate.Confirmed && !previous.Confirmed;

    private static string? RelativeDirectory(string relative)
    {
        if (!ValidText(relative, MaximumKeyLength) || Path.IsPathRooted(relative) || relative.Contains('|')) return null;
        string portable = relative.Replace('\\', '/');
        if (portable.EndsWith('/') || portable.Contains(':')) return null;
        var parts = new List<string>();
        foreach (string part in portable.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return parts.Count == 0 ? null : parts.Count == 1 ? "." : string.Join('/', parts.Take(parts.Count - 1));
    }

    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
}
