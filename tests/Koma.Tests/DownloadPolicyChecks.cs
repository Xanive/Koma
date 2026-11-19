using Koma;

internal static class DownloadPolicyChecks
{
    public static void Run(string scratch)
    {
        var root = new Folder(Guid.NewGuid(), scratch);
        string partial = Path.Combine(scratch, ".koma-incomplete", "job", "Episode 01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!); File.WriteAllText(partial, "partial fixture");
        string final = Path.Combine(scratch, "Example", "Episode 02.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "finished fixture");
        var scan = Library.Scan([root], default).Single();
        Require(scan.Collections.SelectMany(x => x.Seasons).SelectMany(x => x.Episodes).Single().Path == final,
            "Incomplete media must never become a playable library entry.");
        Require(Library.Expand([partial, Path.GetDirectoryName(partial)!, final], [root], default).Single().Path == final,
            "Direct file/folder drops must not bypass incomplete-download isolation.");
        Require(Library.IsIncompletePath(@"C:\Anime\.KOMA-INCOMPLETE\job\episode.mkv") &&
            !Library.IsIncompletePath(@"C:\Anime\Completed\episode.mkv"), "Staging exclusion should be exact and case-insensitive.");
        var uri = DownloadSources.NyaaFeed("A title & other + 日本語", "1_2");
        Require(uri.Scheme == "https" && uri.Host == "nyaa.media" && uri.Query.Contains("page=rss") &&
            uri.Query.Contains("c=1_2") && uri.Query.Contains("%26"), "Nyaa searches must encode title text on the user's selected host.");
        try { DownloadSources.NyaaFeed("Anime", "software"); throw new Exception("Bad category accepted"); } catch (ArgumentException) { }
        Require(DownloadSources.Quality("[Test] Example [1080P]") == "1080p", "Release quality filtering must ignore case.");
        var profile = Path.Combine(scratch, "profile");
        var settings = new PreferenceStore(profile, Path.Combine(scratch, "missing"));
        Require(settings.Data.DownloadLimitKiB == 0 && settings.Data.UploadLimitKiB == 1024,
            "Downloads should have a predictable default upload cap.");
        settings.Data.DownloadFolder = scratch; settings.Data.DownloadLimitKiB = 5120; settings.Data.UploadLimitKiB = 128;
        settings.Save(); var restored = new PreferenceStore(profile, Path.Combine(scratch, "missing"));
        Require(restored.Data.DownloadFolder == scratch && restored.Data.DownloadLimitKiB == 5120 && restored.Data.UploadLimitKiB == 128,
            "Destination and bandwidth preferences should survive restart.");
        settings.Data.ManagedDownloadFolders = [new(root.Id, "Frieren - 05-abcdef12")];
        var managedEpisode = new Episode(Path.Combine(scratch, "Frieren - 05-abcdef12", "Frieren - 05.mkv"), root.Id, @"Frieren - 05-abcdef12\Frieren - 05.mkv");
        var detected = DownloadSources.Detect(managedEpisode, settings.Data, "Frieren - 05-abcdef12", "Frieren - 05-abcdef12");
        Require(detected is { Title: "Frieren", Episode: 5, NeedsReview: false }, "Koma's generated download-container name must not contradict a clear release filename.");
        settings.Save(); restored = new(profile, Path.Combine(scratch, "missing"));
        Require(DownloadSources.Detect(managedEpisode, restored.Data).NeedsReview == false,
            "Managed publication identities must survive queue removal and restarts.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
