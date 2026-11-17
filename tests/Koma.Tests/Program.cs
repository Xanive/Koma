using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Koma;

var checks = new (string Name, Action<string> Body)[]
{
    ("Keeps interface translations complete and preserves their format placeholders", CheckTranslations),
    ("Resolves every localized XAML caption", CheckLocalizedXaml),
    ("Persists the chosen interface language and recovers unsupported choices", CheckInterfaceLanguage),
    ("Finds covers in show-first order and invalidates changed thumbnail sources", CheckArtworkPolicy),
    ("Discovers prelaunch videos, preserves seasons, and sorts Unicode names", CheckDiscovery),
    ("Discovers newly added episodes without changing a previous scan", CheckRefresh),
    ("Deduplicates file and folder inputs with a stable progress identity", CheckOverlappingSources),
    ("Canonicalizes relative drops and ignores unsupported input files", CheckDropInputs),
    ("Keeps progress when a saved folder is unavailable and relocated", CheckRelocation),
    ("Skips child junctions or directory symlinks", CheckReparsePoint),
    ("Preserves progress and defaults across settings round trips", CheckRoundTrip),
    ("Carries installed beta settings into Koma and prefers the current profile", CheckRenamedProfile),
    ("Recovers a backup and preserves malformed settings", CheckBackupRecovery),
    ("Recovers a backup when settings contain invalid UTF-8", CheckEncoding),
    ("Keeps useful settings when folder or history entries are malformed", CheckSettingsNormalization),
    ("Imports compatible settings and history from the prototype", CheckLegacyImport),
    ("Scans a 1,000-episode collection and honors cancellation", CheckLargeScan)
};

int failed = 0, skipped = 0;
foreach (var (name, check) in checks)
{
    var scratch = Directory.CreateTempSubdirectory("koma-tests-");
    try
    {
        check(scratch.FullName);
        Console.WriteLine($"PASS {name}");
    }
    catch (SkippedCheckException error)
    {
        skipped++;
        Console.WriteLine($"SKIP {name} ({error.Message})");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{error}");
    }
    finally
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(scratch.FullName));
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (string.Equals(parent, temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            scratch.Name.StartsWith("koma-tests-", StringComparison.Ordinal))
            scratch.Delete(recursive: true);
    }
}

Console.WriteLine($"{checks.Length - failed - skipped} passed, {failed} failed, {skipped} skipped");
return failed == 0 ? 0 : 1;

static void CheckTranslations(string scratch)
{
    var english = UiText.Catalog("en");
    foreach (string language in new[] { "de", "fr" })
    {
        var translated = UiText.Catalog(language);
        Require(english.Keys.Order().SequenceEqual(translated.Keys.Order()), "Translations should cover the same interface strings.");
        foreach (var (key, value) in translated)
        {
            Require(!string.IsNullOrWhiteSpace(value) && !value.Contains('\ufffd'), "A translated caption is missing or has invalid characters.");
            string[] originalFields = System.Text.RegularExpressions.Regex.Matches(key, @"\{\d+[^}]*\}").Select(match => match.Value).Order().ToArray();
            string[] translatedFields = System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+[^}]*\}").Select(match => match.Value).Order().ToArray();
            Require(originalFields.SequenceEqual(translatedFields), "A translated caption changed its formatting placeholders.");
        }
        UiText.Select(language);
        Require(UiText.Translate("Your library") != "Your library", "The requested interface language did not activate.");
        Require(UiText.Episodes(2).Contains('2'), "Localized episode counts should retain their number.");
    }
    UiText.Select("unknown");
    Require(UiText.Language == "en" && UiText.Translate("Your library") == "Your library", "An unsupported language should use English.");
}

static void CheckLocalizedXaml(string scratch)
{
    foreach (string name in new[] { "MainWindow", "Controls" })
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"Koma.Test.{name}.xaml")!;
        using var reader = new StreamReader(stream);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), @"\{DynamicResource Koma.Text.([^}]+)\}"))
            Require(UiText.Catalog("en").ContainsKey(match.Groups[1].Value), "A localized XAML resource does not exist.");
    }
}

static void CheckInterfaceLanguage(string scratch)
{
    string legacy = Path.Combine(scratch, "prototype.json");
    var store = new PreferenceStore(scratch, legacy);
    Require(store.Data.UiLanguage == "en", "Existing users should start with English.");
    store.Data.UiLanguage = "de";
    store.Save();
    Require(new PreferenceStore(scratch, legacy).Data.UiLanguage == "de", "The chosen interface language should survive a restart.");
    store.Data.UiLanguage = "fr";
    store.Save();
    Require(new PreferenceStore(scratch, legacy).Data.UiLanguage == "fr", "French should persist too.");
    store.Data.UiLanguage = "unknown";
    store.Save();
    Require(new PreferenceStore(scratch, legacy).Data.UiLanguage == "en", "Unsupported interface choices should recover to English.");
}

static void CheckArtworkPolicy(string scratch)
{
    string show = Path.Combine(scratch, "Show");
    string season = Path.Combine(show, "Season 1");
    Directory.CreateDirectory(season);
    string[] expected = [Path.Combine(show, "cover.jpg"), Path.Combine(show, "folder.png"), Path.Combine(season, "cover.png")];
    foreach (string file in expected) File.WriteAllText(file, "fixture");
    Require(ArtworkPolicy.Covers(show, season).SequenceEqual(expected), "Show covers should take priority over season covers.");
    Require(ArtworkPolicy.Covers(show, show).Count() == 2, "The same directory should not be searched twice.");
    string video = Path.Combine(season, "Episode 02.mkv");
    File.WriteAllText(video, "one");
    string first = ArtworkPolicy.Key(video);
    File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddMinutes(-10));
    string changedTime = ArtworkPolicy.Key(video);
    Require(first != changedTime, "A changed source timestamp should invalidate the thumbnail.");
    var timestamp = File.GetLastWriteTimeUtc(video);
    File.WriteAllText(video, "a different size");
    File.SetLastWriteTimeUtc(video, timestamp);
    Require(changedTime != ArtworkPolicy.Key(video), "A changed source size should invalidate the thumbnail.");
}

static void CheckDiscovery(string scratch)
{
    var root = new Folder(Guid.NewGuid(), Path.Combine(scratch, "Anime"), IsDefault: true);
    AddFile(root.Path, "映画 Night.mp4");
    AddFile(root.Path, "風景.png");
    AddFile(root.Path, "Frieren", "Season 1", "第10話.MKV");
    AddFile(root.Path, "Frieren", "Season 1", "第02話.mkv");
    AddFile(root.Path, "Frieren", "Season 1", "第1話.mp4");
    AddFile(root.Path, "Frieren", "Season 1", "第1話.ass");
    AddFile(root.Path, "Frieren", "Season 2", "第1話.mkv");

    ScannedFolder entry = Scan(root).Single();
    Require(entry.Available, "The populated folder should be available.");
    Require(entry.Label == "Anime folder", "Default folder label is incorrect.");
    Require(entry.Collections.Select(group => group.Name).SequenceEqual(["Loose videos", "Frieren"]),
        "Loose videos and show names should be grouped separately.");
    Require(entry.Collections[0].Seasons.Single().Episodes.Single().Name == "映画 Night",
        "Image files should be ignored and Unicode names retained.");
    var seasons = entry.Collections[1].Seasons;
    Require(seasons.Select(season => season.Name).SequenceEqual(["Season 1", "Season 2"]),
        "Season names should match their folder names.");
    Require(seasons[0].Episodes.Select(episode => episode.Name).SequenceEqual(["第1話", "第02話", "第10話"]),
        "Episodes should sort by number, with padding and non-Latin text preserved.");
    var episode = seasons[0].Episodes[0];
    Require(episode.FolderId == root.Id && episode.Relative == Path.Combine("Frieren", "Season 1", "第1話.mp4"),
        "Saved-folder progress should use the root ID and a relative path.");
}

static void CheckRefresh(string scratch)
{
    Folder root = CreateRoot(scratch);
    var separateScan = Scan(root).Single();
    Require(separateScan.Available && separateScan.Collections.Length == 0, "An empty folder is still available.");

    AddFile(root.Path, "Series", "Season 1", "Episode 02.mkv");
    var previous = Episodes(Scan(root));
    AddFile(root.Path, "Series", "Season 1", "Episode 10.mkv");
    var refreshed = Episodes(Scan(root));

    Require(previous.Length == 1 && refreshed.Length == 2 && refreshed[1].Name == "Episode 10",
        "A new scan should discover added episodes without modifying the previous result.");
}

static void CheckOverlappingSources(string scratch)
{
    string path = AddFile(scratch, "Anime", "Series", "Season 1", "Episode 02.mkv");
    var parent = new Folder(Guid.NewGuid(), Path.Combine(scratch, "Anime"));
    var nested = new Folder(Guid.NewGuid(), Path.Combine(parent.Path, "Series"));
    var duplicate = new Folder(Guid.NewGuid(), parent.Path);
    Folder[] roots = [parent, nested, duplicate];

    Episode scanned = Episodes(Scan(roots)).Single();
    Episode dropped = Library.Expand([path, parent.Path, nested.Path, path], roots, CancellationToken.None).Single();
    Require(string.Equals(scanned.Path, dropped.Path, StringComparison.OrdinalIgnoreCase), "The same episode should be discovered from each entry point.");
    Require(scanned.Key == dropped.Key, "Browsing and dropping overlapping roots should yield the same progress key.");

    var external = Library.Identify(Path.Combine(scratch, "Anime Extras", "Episode.mkv"), roots);
    Require(external.FolderId is null, "A sibling with a shared path prefix must stay outside the saved root.");
}

static void CheckRelocation(string scratch)
{
    var root = new Folder(Guid.NewGuid(), Path.Combine(scratch, "Old"));
    AddFile(root.Path, "Series", "Season 2", "Episode 09.mkv");
    Episode oldEpisode = Episodes(Scan(root)).Single();
    var store = NewStore(scratch);
    store.Data.Folders.Add(root);
    store.Data.History[oldEpisode.Key] = new(300, 1440, false, DateTimeOffset.UtcNow);
    store.Save();

    string next = Path.GetFullPath(Path.Combine(scratch, "New"));
    Require(Path.GetDirectoryName(next) == scratch && Path.GetDirectoryName(Path.GetFullPath(root.Path)) == scratch,
        "Relocation must stay within the temporary test directory.");
    Directory.Move(root.Path, next);
    ScannedFolder offline = Scan(root).Single();
    Require(!offline.Available && offline.Collections.Length == 0 && offline.Folder.Id == root.Id,
        "A disconnected directory should remain in the saved-folder list.");

    var reopened = NewStore(scratch);
    Folder relocated = reopened.Data.Folders.Single() with { Path = next };
    Episode newEpisode = Episodes(Scan(relocated)).Single();
    Require(newEpisode.Path != oldEpisode.Path && newEpisode.Key == oldEpisode.Key,
        "Repointing a saved folder should preserve the episode identity.");
    Require(reopened.Data.History[newEpisode.Key].Seconds == 300, "Resume position must survive relocation.");
}

static void CheckDropInputs(string scratch)
{
    Folder root = CreateRoot(scratch);
    string first = AddFile(root.Path, "Episode 02.MKV");
    AddFile(root.Path, "Episode 10.mp4");
    string unsupported = AddFile(root.Path, "Playlist.m3u");
    string previous = Environment.CurrentDirectory;
    Episode[] dropped;
    try
    {
        Environment.CurrentDirectory = scratch;
        dropped = Library.Expand([".\\Anime\\", first, unsupported, "missing.mp4", "bad\0path"], [root], CancellationToken.None);
    }
    finally { Environment.CurrentDirectory = previous; }

    Require(dropped.Select(episode => episode.Name).SequenceEqual(["Episode 02", "Episode 10"]),
        "Relative paths, duplicates, invalid paths, and unsupported files should leave two ordered videos.");
    Require(dropped.All(episode => Path.IsPathFullyQualified(episode.Path)), "All accepted paths must be absolute.");
}

static void CheckReparsePoint(string scratch)
{
    Folder root = CreateRoot(scratch);
    AddFile(root.Path, "Valid", "Episode 01.mp4");
    AddFile(scratch, "Outside", "Episode 99.mp4");
    string link = Path.Combine(root.Path, "Skip Me");
    CreateDirectoryLink(link, Path.Combine(scratch, "Outside"));
    try
    {
        Require(Episodes(Scan(root)).Single().Name == "Episode 01", "Library scan followed a directory link.");
        Require(Library.Expand([root.Path], [root], CancellationToken.None).Single().Name == "Episode 01",
            "Folder drop followed a directory link.");
    }
    finally
    {
        Directory.Delete(link);
    }
}

static void CheckRoundTrip(string scratch)
{
    var store = NewStore(scratch);
    var data = store.Data;
    Require(data.SeekStep == 5 && data.VolumeStep == 5 && data.FullscreenDelay == 3 && data.Quality == "Auto",
        "Fresh settings should have the agreed defaults.");
    Require(data.Resume && data.Autoplay, "Resume and autoplay should be on by default.");
    data.Quality = "C";
    data.AudioLanguage = "jpn";
    data.SubtitleLanguage = "eng";
    data.SeekStep = 12;
    data.VolumeStep = 3;
    data.FullscreenDelay = 8;
    data.Resume = false;
    data.Autoplay = false;
    var root = new Folder(Guid.NewGuid(), Path.Combine(scratch, "アニメ"));
    data.Folders.Add(root);
    string key = new Episode(Path.Combine(root.Path, "Épisode 1.mkv"), root.Id, "Épisode 1.mkv").Key;
    data.History[key] = new(1440, 1440, true, DateTimeOffset.UtcNow);
    store.Save();

    var reopened = NewStore(scratch).Data;
    Require(reopened.Folders.Single() == root && reopened.History[key.ToUpperInvariant()].Watched,
        "Saved folders and case-insensitive watched entries should survive a round trip.");
    Require(reopened.Quality == "C" && reopened.AudioLanguage == "jpn" && reopened.SubtitleLanguage == "eng" &&
        reopened.SeekStep == 12 && reopened.VolumeStep == 3 && reopened.FullscreenDelay == 8 &&
        !reopened.Resume && !reopened.Autoplay, "Playback settings did not round trip.");
}

static void CheckRenamedProfile(string scratch)
{
    string previous = Path.Combine(scratch, "Local Anime Player");
    string current = Path.Combine(scratch, "Koma");
    string prototype = Path.Combine(scratch, "prototype-settings.json");
    var oldStore = new PreferenceStore(previous, prototype);
    var folder = new Folder(Library.DefaultFolderId, Path.Combine(scratch, "Anime"), true);
    oldStore.Data.Folders.Add(folder);
    var episode = Library.Identify(Path.Combine(folder.Path, "Episode 02.mkv"), [folder]);
    oldStore.Data.History[episode.Key] = new(89, 1400, false, DateTimeOffset.UtcNow);
    oldStore.Data.SeekStep = 12;
    oldStore.Save();
    oldStore.Save();
    File.WriteAllText(Path.Combine(previous, "settings.json"), "broken");

    var migrated = new PreferenceStore(current, prototype, previous);
    Require(migrated.Data.Folders.Single() == folder && migrated.Data.History[episode.Key].Seconds == 89 &&
        migrated.Data.SeekStep == 12, "The previous profile backup should supply folders, history, and playback preferences.");
    Require(File.ReadAllText(Path.Combine(previous, "settings.json")) == "broken", "Reading a previous profile should preserve its files.");
    migrated.Data.SeekStep = 8;
    migrated.Save();
    Require(new PreferenceStore(current, prototype, previous).Data.SeekStep == 8, "Koma should prefer its own profile after saving.");
}

static void CheckBackupRecovery(string scratch)
{
    var store = NewStore(scratch);
    store.Data.SeekStep = 11;
    store.Save();
    store.Data.SeekStep = 14;
    store.Save();

    string settings = Path.Combine(scratch, "settings.json");
    const string corrupted = "{ unfinished json";
    File.WriteAllText(settings, corrupted);
    var recovered = NewStore(scratch);
    Require(recovered.Data.SeekStep == 11, "The backup should restore the last intact saved version.");
    Require(recovered.Notice is not null, "Recovering a damaged profile should provide a notice.");
    string preserved = Directory.GetFiles(scratch, "settings.json.damaged-*").Single();
    Require(File.ReadAllText(preserved) == corrupted, "The malformed profile should be kept for recovery.");

    recovered.Save();
    Require(NewStore(scratch).Data.SeekStep == 11, "The recovered settings should be reusable on the next launch.");
    Require(Directory.GetFiles(scratch, "*.tmp").Length == 0, "A save must not leave temporary files behind.");
}

static void CheckEncoding(string scratch)
{
    var store = NewStore(scratch);
    store.Data.SeekStep = 12;
    store.Save();
    store.Data.SeekStep = 19;
    store.Save();

    string path = Path.Combine(scratch, "settings.json");
    byte[] malformed = [.. "{\"Version\":1,\"AudioLanguage\":\""u8, 0xc3, 0x28, .. "\"}"u8];
    File.WriteAllBytes(path, malformed);
    var recovered = NewStore(scratch);
    Require(recovered.Data.SeekStep == 12 && recovered.Notice is not null,
        "Invalid encoded bytes should be caught before they can become a replacement character.");
    string preserved = Directory.GetFiles(scratch, "settings.json.damaged-*").Single();
    Require(File.ReadAllBytes(preserved).SequenceEqual(malformed), "Recovery should preserve the input bytes.");

    File.WriteAllText(path, "{\"Version\":1,\"AudioLanguage\":\"日本語\",\"SeekStep\":15}", Encoding.Unicode);
    var unicode = NewStore(scratch).Data;
    Require(unicode.SeekStep == 15 && unicode.AudioLanguage == "日本語",
        "A valid UTF-16 profile with a byte-order mark should retain its settings.");
}

static void CheckSettingsNormalization(string scratch)
{
    string defaultPath = Path.Combine(scratch, "Anime");
    string extraPath = Path.Combine(scratch, "More Anime");
    string video = Path.Combine(defaultPath, "Épisode.mkv");
    var extraId = Guid.NewGuid();
    DateTimeOffset earlier = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
    DateTimeOffset later = earlier.AddHours(1);
    var settings = new
    {
        Version = 1,
        SeekStep = 9,
        Quality = "invalid",
        AudioLanguage = (string?)null,
        Folders = new object?[]
        {
            null,
            new Folder(Guid.NewGuid(), defaultPath.ToUpperInvariant()),
            new Folder(Library.DefaultFolderId, defaultPath + Path.DirectorySeparatorChar),
            new Folder(Library.DefaultFolderId, Path.Combine(scratch, "Duplicate ID")),
            new Folder(extraId, extraPath, true),
            new Folder(Guid.NewGuid(), "relative"),
            new Folder(Guid.NewGuid(), ""),
            new Folder(Guid.Empty, Path.Combine(scratch, "Empty ID"))
        },
        History = new Dictionary<string, Progress?>
        {
            [video.ToUpperInvariant()] = new(25, 100, false, earlier),
            [video] = new(35, 100, false, later),
            [Path.Combine(defaultPath, "Bad.mkv")] = null,
            [Path.Combine(defaultPath, "Negative.mkv")] = new(-1, 100, false, later)
        }
    };
    File.WriteAllText(Path.Combine(scratch, "settings.json"), JsonSerializer.Serialize(settings));

    var data = NewStore(scratch).Data;
    Require(data.SeekStep == 9 && data.Quality == "Auto" && data.AudioLanguage == "",
        "Normalize only invalid settings; preserve the configured seek step.");
    Require(data.Folders.SequenceEqual([new(Library.DefaultFolderId, defaultPath, true), new(extraId, extraPath)]),
        "The default directory should win duplicates without dropping the valid custom folder.");
    Require(data.History.Count == 1 && data.History[video].Seconds == 35,
        "Invalid progress should be filtered and the most recent case-insensitive entry retained.");
}

static void CheckLegacyImport(string scratch)
{
    string video = Path.Combine(scratch, "夏 01.mkv");
    File.WriteAllText(Path.Combine(scratch, "prototype.json"), JsonSerializer.Serialize(new
    {
        SeekStep = 9.0,
        VolumeStep = 4,
        FullscreenDelay = 7,
        Volume = 69.5,
        Mode = 2,
        Autoplay = false,
        Resume = true,
        Positions = new Dictionary<string, double> { [video] = 317.5, [Path.Combine(scratch, "Ignore.mkv")] = 0 }
    }));
    var data = NewStore(scratch).Data;
    Require(data.SeekStep == 9 && data.VolumeStep == 4 && data.FullscreenDelay == 7 && data.Volume == 70 &&
        !data.Autoplay && data.Resume, "Compatible prototype controls should be imported.");
    Require(data.Quality is "B" or "Mode B", "The prototype's selected B preset should be migrated.");
    Require(data.History.Count == 1 && data.History[video.ToUpperInvariant()] is { Seconds: 317.5, Watched: false },
        "Legacy progress should retain the video path and time, with invalid positions omitted.");
}

static void CheckLargeScan(string scratch)
{
    Folder root = CreateRoot(scratch);
    for (int show = 1; show <= 40; show++)
        for (int episode = 25; episode > 0; episode--)
            AddFile(root.Path, $"Show {show}", "Season 1", $"Episode {episode}.mp4");

    Episode[] episodes = Episodes(Scan(root));
    Require(episodes.Length == 1000 && episodes[0].Name == "Episode 1" && episodes[^1].Name == "Episode 25",
        "The scanner should discover all episodes and retain their natural order.");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try
    {
        Library.Scan([root], canceled.Token);
        throw new InvalidOperationException("A canceled scan should stop.");
    }
    catch (OperationCanceledException) { }
}

static PreferenceStore NewStore(string path) => new(path, Path.Combine(path, "prototype.json"));
static ScannedFolder[] Scan(params Folder[] roots) => Library.Scan(roots, CancellationToken.None);
static Episode[] Episodes(ScannedFolder[] roots) =>
    roots.SelectMany(root => root.Collections).SelectMany(group => group.Seasons).SelectMany(season => season.Episodes).ToArray();

static Folder CreateRoot(string scratch)
{
    var root = new Folder(Guid.NewGuid(), Path.Combine(scratch, "Anime"));
    Directory.CreateDirectory(root.Path);
    return root;
}

static string AddFile(string root, params string[] relative)
{
    string path = Path.Combine([root, .. relative]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using (File.Create(path)) { }
    return path;
}

static void CreateDirectoryLink(string link, string target)
{
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(link, target);
        return;
    }
    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
    foreach (string item in new[] { "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:LAP_TEST_LINK -Target $env:LAP_TEST_TARGET | Out-Null" })
        start.ArgumentList.Add(item);
    start.Environment["LAP_TEST_LINK"] = link;
    start.Environment["LAP_TEST_TARGET"] = target;
    using var process = Process.Start(start) ?? throw new SkippedCheckException("Could not start the junction helper.");
    process.WaitForExit();
    if (process.ExitCode != 0) throw new SkippedCheckException("Windows would not create a junction in the test directory.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class SkippedCheckException(string message) : Exception(message);
