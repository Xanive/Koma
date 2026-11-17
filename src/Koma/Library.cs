using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Koma;

internal record Folder(Guid Id, string Path, bool IsDefault = false)
{
    [JsonIgnore]
    public string Name => IsDefault ? "Anime folder" : new DirectoryInfo(Path).Name;
}

internal record Episode(string Path, Guid? FolderId, string Relative)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Key => FolderId is null ? Path : $"{FolderId:N}/{Relative}";
    public override string ToString() => Name;
}

internal record Progress(double Seconds, double Duration, bool Watched, DateTimeOffset Updated);
internal record Season(string Name, Episode[] Episodes);
internal record Collection(string Name, Season[] Seasons);
internal record ScannedFolder(Folder Folder, bool Available, Collection[] Collections)
{
    public string Label => Available ? Folder.Name : $"{Folder.Name} · unavailable";
}

internal sealed class Preferences
{
    public int Version { get; set; } = 1;
    public int SeekStep { get; set; } = 5;
    public int VolumeStep { get; set; } = 5;
    public int FullscreenDelay { get; set; } = 3;
    public int Volume { get; set; } = 100;
    public bool Autoplay { get; set; } = true;
    public bool Resume { get; set; } = true;
    public string Quality { get; set; } = "Auto";
    public string AudioLanguage { get; set; } = "";
    public string SubtitleLanguage { get; set; } = "";
    public List<Folder> Folders { get; set; } = [];
    public Dictionary<string, Progress> History { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PreferenceStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    private static readonly Encoding[] StrictUnicode =
    [
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
        Utf8, new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)
    ];
    private readonly string file;
    public Preferences Data { get; }
    public string? Notice { get; private set; }

    public PreferenceStore(string directory, string legacy, string? previousDirectory = null)
    {
        file = Path.Combine(directory, "settings.json");
        try
        {
            Data = ReadSaved(file) ??
                (previousDirectory is null ? null : ReadSaved(Path.Combine(previousDirectory, "settings.json"))) ??
                Import(legacy);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Data = new();
            Notice = "Saved settings could not be read. Default settings are active.";
        }
        Normalize();
    }

    private Preferences? ReadSaved(string path) => Read(path) ?? Read(path + ".bak");

    private void Normalize()
    {
        Data.SeekStep = Math.Clamp(Data.SeekStep, 1, 120);
        Data.VolumeStep = Math.Clamp(Data.VolumeStep, 1, 25);
        Data.FullscreenDelay = Math.Clamp(Data.FullscreenDelay, 1, 15);
        Data.Volume = Math.Clamp(Data.Volume, 0, 100);
        Data.AudioLanguage ??= "";
        Data.SubtitleLanguage ??= "";
        if (Data.Quality is not ("Auto" or "A" or "B" or "C" or "Off")) Data.Quality = "Auto";

        var folders = new List<Folder>();
        var folderIds = new HashSet<Guid>();
        var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Folder? root in (Data.Folders ?? []).OrderByDescending(x => x?.Id == Library.DefaultFolderId))
        {
            if (root is not { Id: var id, Path: { } path } || id == Guid.Empty || !Path.IsPathFullyQualified(path) ||
                Library.FullPathOrNull(path) is not { } absolute || folderIds.Contains(id) || folderPaths.Contains(absolute))
                continue;
            folderIds.Add(id);
            folderPaths.Add(absolute);
            folders.Add(new(id, absolute, id == Library.DefaultFolderId));
        }
        Data.Folders = folders;

        var history = new Dictionary<string, Progress>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, progress) in Data.History ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || progress is null ||
                !double.IsFinite(progress.Seconds) || progress.Seconds < 0 ||
                !double.IsFinite(progress.Duration) || progress.Duration < 0)
                continue;
            if (!history.TryGetValue(key, out var previous) || progress.Updated >= previous.Updated)
                history[key] = progress;
        }
        Data.History = history;
    }

    private Preferences? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(ReadJson(path));
            if (saved?.Version == 1) return saved;
            if (path == file)
                File.Move(path, path + ".version-" + Guid.NewGuid().ToString("N"));
            Notice = "Settings version was not recognized. Using available defaults or backup.";
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            if (path == file)
                File.Move(path, path + ".damaged-" + Guid.NewGuid().ToString("N"));
            Notice = "Recovered from damaged settings. The original file was preserved when possible.";
        }
        return null;
    }

    private static Preferences Import(string path)
    {
        var data = new Preferences();
        if (!File.Exists(path)) return data;
        using var json = JsonDocument.Parse(ReadJson(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return data;
        data.SeekStep = Integer("SeekStep", 5);
        data.VolumeStep = Integer("VolumeStep", 5);
        data.FullscreenDelay = Integer("FullscreenDelay", 3);
        data.Volume = Integer("Volume", 100);
        data.Autoplay = !root.TryGetProperty("Autoplay", out var autoplay) || autoplay.ValueKind != JsonValueKind.False;
        data.Resume = !root.TryGetProperty("Resume", out var resume) || resume.ValueKind != JsonValueKind.False;
        int mode = Integer("Mode", -1);
        if (mode is >= 0 and <= 3) data.Quality = new[] { "Off", "A", "B", "C" }[mode];
        if (root.TryGetProperty("Positions", out var positions) && positions.ValueKind == JsonValueKind.Object)
            foreach (var entry in positions.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out var seconds) && seconds > 0)
                    data.History[entry.Name] = new(seconds, 0, false, DateTimeOffset.UtcNow);
        return data;

        int Integer(string name, int fallback) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && number is >= int.MinValue and <= int.MaxValue
                ? (int)Math.Round(number) : fallback;
    }

    private static string ReadJson(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        foreach (Encoding encoding in StrictUnicode)
            if (bytes.AsSpan().StartsWith(encoding.Preamble))
                return encoding.GetString(bytes.AsSpan(encoding.Preamble.Length));
        return Utf8.GetString(bytes);
    }

    public void Save()
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Data));
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak", true);
            else File.Move(temporary, file);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static partial class Library
{
    internal static readonly Guid DefaultFolderId = new("d5fb3ea4-c14b-4130-ac46-8c419d8cdd8a");
    public const string Filter = "Video files|*.mkv;*.mp4;*.m4v;*.webm;*.mov;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg";
    private static readonly HashSet<string> Extensions = new(
        [".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".mpg", ".mpeg"], StringComparer.OrdinalIgnoreCase);
    private static readonly EnumerationOptions Enumeration = new() {
        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };
    internal static readonly IComparer<string> Natural = Comparer<string>.Create(CompareNames);

    public static string DefaultPath()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(videos.Length == 0
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos")
            : videos, "Anime");
    }

    public static Episode Identify(string path, IEnumerable<Folder> roots)
    {
        string full = Path.GetFullPath(path);
        foreach (Folder root in roots)
        {
            string relative = Path.GetRelativePath(root.Path, full);
            if (relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return new(full, root.Id, relative);
        }
        return new(full, null, full);
    }

    public static ScannedFolder[] Scan(Folder[] roots, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return roots.Select(root => ScanOne(root, seen, token)).ToArray();
    }

    private static ScannedFolder ScanOne(Folder root, HashSet<string> seen, CancellationToken token)
    {
        if (!Directory.Exists(root.Path)) return new(root, false, []);
        var groups = new List<Collection>();
        Episode[] loose = ReadFiles(root.Path, root, seen);
        if (loose.Length != 0) groups.Add(new("Loose videos", [new("Loose videos", loose)]));

        foreach (string show in ReadDirectories(root.Path))
        {
            var seasons = new List<Season>();
            foreach (string path in Walk(show, token))
            {
                Episode[] files = ReadFiles(path, root, seen);
                if (files.Length == 0) continue;
                string relative = Path.GetRelativePath(show, path);
                seasons.Add(new(relative == "." ? Path.GetFileName(show) : relative, files));
            }
            if (seasons.Count != 0) groups.Add(new(Path.GetFileName(show), [.. seasons]));
        }
        return new(root, true, [.. groups]);
    }

    public static Episode[] Expand(string[] inputs, Folder[] roots, CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var episodes = new List<Episode>();
        foreach (string input in inputs)
        {
            token.ThrowIfCancellationRequested();
            if (FullPathOrNull(input) is not { } path) continue;
            if (IsSupportedVideo(path) && File.Exists(path) && files.Add(path))
                episodes.Add(Identify(path, roots));
            else if (Directory.Exists(path))
                foreach (string directory in Walk(path, token))
                    foreach (string video in Files(directory))
                        if (files.Add(video)) episodes.Add(Identify(video, roots));
        }
        return [.. episodes];
    }

    internal static string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static IEnumerable<string> Walk(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            yield return path;
            foreach (string child in ReadDirectories(path).Reverse()) pending.Push(child);
        }
    }

    private static Episode[] ReadFiles(string path, Folder root, HashSet<string> seen) =>
        Files(path).Where(seen.Add).Select(file => new Episode(file, root.Id, Path.GetRelativePath(root.Path, file))).ToArray();

    private static string[] Files(string path) =>
        Entries(() => Directory.EnumerateFiles(path, "*", Enumeration))
            .Where(IsSupportedVideo).OrderBy(file => Path.GetFileName(file)!, Natural).ToArray();

    private static bool IsSupportedVideo(string path) => Extensions.Contains(Path.GetExtension(path));

    private static string[] ReadDirectories(string path) =>
        Entries(() => Directory.EnumerateDirectories(path, "*", Enumeration)).OrderBy(path => Path.GetFileName(path)!, Natural).ToArray();

    private static string[] Entries(Func<IEnumerable<string>> enumerate)
    {
        try { return enumerate().ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    [GeneratedRegex(@"[0-9]+|[^0-9]+")]
    private static partial Regex NameParts();

    private static int CompareNames(string? left, string? right)
    {
        if (left is null || right is null) return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        var a = NameParts().Matches(left);
        var b = NameParts().Matches(right);
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            string x = a[i].Value, y = b[i].Value;
            if (char.IsAsciiDigit(x[0]) && char.IsAsciiDigit(y[0]))
            {
                x = x.TrimStart('0');
                y = y.TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
            }
            int order = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            if (order != 0) return order;
        }
        int count = a.Count.CompareTo(b.Count);
        return count != 0 ? count : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
}
