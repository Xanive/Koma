using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Koma;

internal sealed record MalAnimeCandidate(int Id, string Title, string[] AlternativeTitles,
    int? StartYear, string MediaType, int EpisodeCount)
{
    public bool SearchIncomplete { get; init; }
    public string EnglishTitle { get; init; } = "";
    public string? PictureUrl { get; init; }
    public string Synopsis { get; init; } = "";
}

internal sealed record AnimeMatchDecision(IReadOnlyList<MalAnimeCandidate> RankedCandidates,
    MalAnimeCandidate? AutomaticMatch)
{
    public MalAnimeCandidate? RecommendedMatch { get; init; }
}

/// <summary>Fuzzy matches are suggestions only. Automatic links require an unambiguous exact title.</summary>
internal static class AnimeMatcher
{
    public static AnimeMatchDecision Rank(DetectedAnime detection, IEnumerable<MalAnimeCandidate> candidates)
    {
        string title = Comparable(detection.Title);
        var ranked = candidates.Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => x.Id).Select(group => group.First() with { SearchIncomplete = group.Any(item => item.SearchIncomplete) })
            .Select(candidate => new { Candidate = candidate, Score = Score(title, candidate),
                Exact = ExactTitle(detection, candidate), Compatible = KindFits(detection, candidate) && SeasonFits(detection, candidate),
                NumberFits = EpisodeFits(detection, candidate) })
            .OrderByDescending(x => x.Compatible).ThenByDescending(x => x.Score).ThenByDescending(x => x.NumberFits)
            .ThenBy(x => x.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Candidate.Id).ToArray();
        MalAnimeCandidate? automatic = null;
        // Type/season metadata can distinguish a movie from a numbered TV episode. Episode counts
        // cannot distinguish remakes: both TV entries with an exact alias still require review.
        var exact = ranked.Where(x => x.Exact && x.Compatible).ToArray();
        if (UsefulTitle(title) &&
            !detection.NeedsReview && detection.Kind != AnimeKind.Special &&
            ranked.All(x => !x.Candidate.SearchIncomplete) &&
            exact.Length == 1 && exact[0].NumberFits &&
            ranked.Where(x => x.Compatible && x.Candidate.Id != exact[0].Candidate.Id).All(x => x.Score < .9))
        {
            automatic = exact[0].Candidate;
        }
        // Recommendations remain visible when manual review is needed. A tie between remakes is
        // intentionally not labelled recommended, and recommendation alone never permits syncing.
        var recommended = ranked.Where(x => x.Compatible && x.NumberFits && x.Score >= .45).ToArray();
        var first = recommended.FirstOrDefault();
        MalAnimeCandidate? suggestion = first is not null &&
            (recommended.Length == 1 || first.Score - recommended[1].Score >= .12) ? first.Candidate : null;
        return new(ranked.Select(x => x.Candidate).ToArray(), automatic) { RecommendedMatch = automatic ?? suggestion };
    }

    private static IEnumerable<string> Names(MalAnimeCandidate candidate) =>
        new[] { candidate.Title, candidate.EnglishTitle }.Concat(candidate.AlternativeTitles ?? []).Where(name => !string.IsNullOrWhiteSpace(name));

    public static bool ExactTitle(DetectedAnime detection, MalAnimeCandidate candidate)
    {
        string title = Comparable(detection.Title);
        return UsefulTitle(title) && Names(candidate).Any(name => Comparable(name) == title);
    }

    public static string MatchReason(DetectedAnime detection, MalAnimeCandidate candidate) =>
        ExactTitle(detection, candidate) ? "Exact title match" : "Closest title";

    internal static bool KindFits(DetectedAnime detection, MalAnimeCandidate candidate) => detection.Kind switch {
        AnimeKind.Movie => candidate.MediaType == "movie",
        AnimeKind.Episode => candidate.MediaType is "tv" or "ona",
        AnimeKind.Unknown => candidate.MediaType is "tv" or "movie" or "ona",
        _ => false
    };

    internal static bool EpisodeFits(DetectedAnime detection, MalAnimeCandidate candidate) =>
        detection.Episode is not > 0 || candidate.EpisodeCount <= 0 || detection.Episode.Value <= candidate.EpisodeCount;

    internal static bool SeasonFits(DetectedAnime detection, MalAnimeCandidate candidate)
    {
        var local = Seasons(detection.Title).Distinct().ToArray();
        int? expected = detection.Season ?? (local.Length == 1 ? local[0] : null);
        if (expected is <= 0 || local.Length > 1 || expected is { } number && local.Any(value => value != number)) return false;
        var provider = Names(candidate).SelectMany(Seasons).Distinct().ToArray();
        if (provider.Length > 1) return false;
        if (expected is > 1) return provider.Length == 1 && provider[0] == expected;
        if (expected == 1) return provider.Length == 0 || provider[0] == 1;
        // An unnumbered file named only with the base-series alias cannot silently become S2.
        return provider.Length == 0 || provider[0] == 1;
    }

    private static bool UsefulTitle(string title) => title.Length > 0 &&
        title is not ("anime" or "videos" or "loose videos" or "episode" or "episodes" or "season");

    private static string Comparable(string text)
    {
        // Preserve every number: 86, a remake year, a season and a part must never collapse together.
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return "";
        string normalized;
        try { normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
        catch (ArgumentException) { return ""; }
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return normalized.Trim();
    }

    private static double Score(string title, MalAnimeCandidate candidate)
    {
        if (title.Length == 0) return 0;
        return Names(candidate).Select(name => Similarity(title, Comparable(name))).DefaultIfEmpty(0).Max();
    }

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return 0;
        double words = (double)a.Intersect(b).Count() / a.Union(b).Count();
        int prefix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) prefix++;
        double prefixScore = (double)prefix / Math.Max(left.Length, right.Length);
        return Math.Min(.98, Math.Max(words, prefixScore));
    }

    private static IEnumerable<int> Seasons(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) yield break;
        foreach (Match match in Regex.Matches(text,
            @"\b(?:season\s*0*(?<season>\d{1,2})|s0*(?<season>\d{1,2})|0*(?<season>\d{1,2})(?:st|nd|rd|th)\s+season)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            if (int.TryParse(match.Groups["season"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) yield return value;
    }
}
