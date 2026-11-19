namespace Koma;

internal sealed record ManagedDownloadFolder(Guid FolderId, string RelativeDirectory);

internal static class DownloadSources
{
    public static List<ManagedDownloadFolder> NormalizeFolders(List<ManagedDownloadFolder>? items) => (items ?? [])
        .Where(item => item is not null && item.FolderId != Guid.Empty && item.RelativeDirectory is { Length: > 0 and < 2000 } &&
            !item.RelativeDirectory.Any(char.IsControl) && !System.IO.Path.IsPathRooted(item.RelativeDirectory) &&
            item.RelativeDirectory.Replace('\\', '/').Split('/').All(x => x.Length > 0 && x is not ("." or "..") && !x.Contains(':')))
        .Select(item => item with { RelativeDirectory = item.RelativeDirectory.Replace('\\', '/').TrimEnd('/') })
        .DistinctBy(item => item.FolderId + "/" + item.RelativeDirectory, StringComparer.OrdinalIgnoreCase).TakeLast(10000).ToList();

    public static DetectedAnime Detect(Episode episode, Preferences settings, string? collection = null, string? season = null)
    {
        string relative = episode.Relative.Replace('\\', '/');
        var managed = settings.ManagedDownloadFolders.FirstOrDefault(item => item.FolderId == episode.FolderId &&
            relative.StartsWith(item.RelativeDirectory + "/", StringComparison.OrdinalIgnoreCase));
        return managed is null ? AnimeRecognition.Detect(episode.Path, collection, season) :
            AnimeRecognition.Detect("Anime/" + relative[(managed.RelativeDirectory.Length + 1)..]);
    }

    public static Uri NyaaFeed(string query, string category = "1_2")
    {
        query = query.Trim();
        if (query.Length > 160 || query.Any(char.IsControl)) throw new ArgumentException("Enter an anime title of up to 160 characters.");
        if (category is not ("1_0" or "1_2" or "1_3" or "1_4")) throw new ArgumentException("Choose an anime category.");
        return new("https://nyaa.media/?page=rss&c=" + category + "&f=0&q=" + Uri.EscapeDataString(query));
    }

    public static string Quality(string title)
    {
        foreach (string quality in new[] { "2160p", "1080p", "720p", "480p" })
            if (title.Contains(quality, StringComparison.OrdinalIgnoreCase)) return quality;
        return "";
    }
}
