using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Koma;

internal enum AnimeKind { Unknown, Episode, Movie, Special }

internal sealed record DetectedAnime(string Title, int? Season, int? Episode, AnimeKind Kind, bool NeedsReview);

/// <summary>Conservative local filename hints; these are not confirmed catalogue identities.</summary>
internal static class AnimeRecognition
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const string EpisodeNumber = @"(?<episode>\d{1,4})(?:v\d+)?";
    private const string AdditionalEpisodes = @"(?:\s*(?:(?:[-~–+&]|to|through)\s*(?:E\s*)?\d|[Ee]\s*\d)|[.,]\d)";
    private const string Technical = @"(?:\b(?:\d{3,4}[pi]|\d{3,4}x\d{3,4}|[248]k|x26[45]|h[ .]?26[45]|hevc|avc|av1|aac|flac|opus|ac3|eac3|dts(?:-hd)?|truehd|hi10p|\d{1,2}[ .-]?bit|web[- .]?dl|webrip|bluray|blu-ray|bd|dvd|bdrip|brrip|hdtv|remux|dual[ .-]?audio|multi[ .-]?(?:audio|sub)|jpn|eng|subbed|dubbed)\b|\b[0-9a-f]{8,40}\b)";
    private static readonly HashSet<string> GenericFolders = new(StringComparer.Ordinal)
    {
        "anime", "videos", "video", "loose videos", "loose files", "episodes", "episode", "downloads", "download",
        "season", "seasons", "tv", "movies", "movie", "unknown", "files", "media", "season 0", "specials", "series",
        "users", "home", "documents", "desktop", "pictures", "music", "temp", "tmp"
    };

    public static DetectedAnime Detect(string filePath, string? collectionName = null, string? seasonName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096)
            return new("", null, null, AnimeKind.Unknown, true);

        string portablePath = filePath.Replace('\\', '/');
        string[] components = portablePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string filename = components.LastOrDefault() ?? "";
        filename = Replace(filename, @"\.(?:mkv|mp4|m4v|webm|mov|avi|ts|m2ts|mpg|mpeg)$", "");
        string cleaned = Clean(filename);
        string fallback = Fallback(collectionName, components);
        int? season = SeasonNumber(seasonName);
        foreach (string folder in components.Reverse().Skip(1).Take(3))
        {
            season ??= SeasonNumber(folder);
            if (GenericFolders.Contains(NormalizeTitle(folder))) break;
        }
        season ??= SeasonNumber(collectionName);
        season ??= SeasonNumber(cleaned);

        bool special = IsMatch(cleaned, @"(?:^|[\s_\-\[\](.])(?:OVA|OAD|ONA|Specials?|SP|NCOP\d*|NCED\d*|OP|ED)(?=$|[\s_\-\[\]).\d])");
        bool movie = IsMatch(cleaned, @"\b(?:Movie|Film|Gekijouban)\b|劇場版");
        int? episode = null;
        bool uncertain = cleaned.Contains('\uFFFD');
        string title = cleaned;

        Match match = Find(cleaned, @"(?<![\p{L}\p{Nd}])S(?<season>\d{1,2})\s*E" + EpisodeNumber);
        if (!match.Success) match = Find(cleaned, @"(?<![\p{L}\p{Nd}])(?<season>\d{1,2})\s*x\s*" + EpisodeNumber);
        if (!match.Success) match = Find(cleaned, @"(?<![\p{L}\p{Nd}])(?:Episode|Ep|E)\s*[ ._-]*" + EpisodeNumber);
        if (!match.Success) match = Find(cleaned, @"\s+-\s*" + EpisodeNumber + @"(?!\d)");

        if (match.Success)
        {
            episode = Positive(match.Groups["episode"].Value);
            if (match.Groups["season"].Success)
            {
                int? explicitSeason = Nonnegative(match.Groups["season"].Value);
                if (season is not null && explicitSeason is not null && season != explicitSeason) uncertain = true;
                season = explicitSeason;
            }
            title = cleaned[..match.Index];
            string after = cleaned[(match.Index + match.Length)..];
            if (IsMatch(after, @"^" + AdditionalEpisodes)) { episode = null; uncertain = true; }
            // The first episode token is enough; later textual episode titles do not become search queries.
            if (episode is null) uncertain = true;
        }
        else if (IsMatch(cleaned, @"^\d{1,4}(?:v\d+)?$"))
        {
            episode = Positive(Find(cleaned, @"^" + EpisodeNumber + "$").Groups["episode"].Value);
            if (episode is >= 1900 and <= 2099) { episode = null; uncertain = true; }
            title = fallback.Length == 0 ? cleaned : "";
            if (fallback.Length == 0) { episode = null; uncertain = true; }
        }
        else if (IsMatch(cleaned, @"^\d{1,4}(?:[.\-~–]\d{1,4})+$"))
        {
            title = "";
            uncertain = true;
        }

        title = CleanTitle(title);
        if (string.IsNullOrWhiteSpace(title) || IsMatch(title, @"^(?:Episode|Ep|E|S\d{1,2})$")) title = fallback;
        else if (episode is not null && !string.IsNullOrEmpty(fallback) && !CompatibleTitles(title, fallback)) uncertain = true;

        if (special || season == 0)
            return new(title, season, null, AnimeKind.Special, true);
        if (movie)
            return new(title, season, null, AnimeKind.Movie, uncertain || episode is not null || string.IsNullOrEmpty(title));
        if (episode is not null)
            return new(title, season, episode, AnimeKind.Episode, uncertain || string.IsNullOrEmpty(title));
        return new(title, season, null, AnimeKind.Unknown, true);
    }

    public static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 4096) return "";
        string normalized = NormalizeUnicode(title).ToLowerInvariant();
        return Replace(normalized, @"[^\p{L}\p{Nd}]+", " ").Trim();
    }

    private static string Clean(string value)
    {
        value = NormalizeUnicode(value).Replace('_', ' ');
        value = Replace(value, @"[\[(](?<tag>[^\[\]()]{1,160})[\])]", match =>
        {
            string tag = match.Groups["tag"].Value;
            string stripped = Replace(tag, Technical, "");
            return NormalizeTitle(stripped).Length == 0 ? " " : match.Value;
        });
        // Initial bracket groups usually name a release group. Preserve bracketed titles when removing
        // them would leave only the episode number (for example [Oshi no Ko] - 01).
        while (Find(value, @"^\s*\[(?<group>[^\]]+)\]\s*(?<rest>.*)$") is { Success: true } leading)
        {
            string rest = leading.Groups["rest"].Value;
            string withoutTechnical = Replace(rest, Technical, "");
            string withoutEpisodes = Replace(withoutTechnical, @"(?:S\d+E\d+|\b(?:Episode|Ep|E)\s*\d+|[- .]*\d+(?:v\d+)?|\[[^\]]*\])", "");
            if (!IsMatch(withoutEpisodes, @"\p{L}")) break;
            value = rest;
        }
        value = Replace(value, Technical, " ");
        value = Replace(value, @"(?<!\d)\.|\.(?!\d)", " ");
        return Replace(value, @"\s+", " ").Trim();
    }

    private static string CleanTitle(string title)
    {
        title = Replace(title, @"[\[\]]", " ");
        title = Replace(title, @"\s+", " ").Trim(' ', '-', '–', '.', '_');
        return title.Length <= 240 ? title : title[..240].Trim();
    }

    private static string Fallback(string? collectionName, string[] components)
    {
        if (Useful(collectionName)) return CleanTitle(Clean(collectionName!));
        foreach (string folder in components.Reverse().Skip(1).Take(3))
        {
            // Season containers may be skipped to find their show. Never climb past a generic
            // library root into a user's profile or unrelated parent directories.
            if (SeasonNumber(folder) is not null) continue;
            if (GenericFolders.Contains(NormalizeTitle(folder))) break;
            if (Useful(folder)) return CleanTitle(Clean(folder));
            break;
        }
        return "";
    }

    private static bool Useful(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512) return false;
        string normalized = NormalizeTitle(title);
        return normalized.Length > 0 && !GenericFolders.Contains(normalized) &&
            !IsMatch(normalized, @"^(?:(?:season|staffel|saison|s)\s*\d+|\d+(?:st|nd|rd|th)?\s*season)$") &&
            !IsMatch(normalized, @"^[a-z]$");
    }

    private static int? SeasonNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return null;
        Match match = Find(value, @"\b(?:season|staffel|saison|s)\s*0*(?<season>\d{1,2})(?!\d)");
        if (!match.Success) match = Find(value, @"\b(?<season>\d{1,2})(?:st|nd|rd|th)?\s+season\b");
        return match.Success ? Nonnegative(match.Groups["season"].Value) : null;
    }

    private static bool CompatibleTitles(string a, string b)
    {
        string x = NormalizeTitle(a), y = NormalizeTitle(b);
        return x == y || x.EndsWith(" " + y, StringComparison.Ordinal) || y.EndsWith(" " + x, StringComparison.Ordinal);
    }

    private static int? Positive(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 ? number : null;
    private static int? Nonnegative(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0 ? number : null;
    private static string NormalizeUnicode(string value)
    {
        try { return value.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return string.Concat(value.EnumerateRunes().Select(x => x.ToString())).Normalize(NormalizationForm.FormKC); }
    }
    private static Match Find(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static bool IsMatch(string text, string pattern) => Find(text, pattern).Success;
    private static string Replace(string text, string pattern, string replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static string Replace(string text, string pattern, MatchEvaluator replacement) => Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
}
