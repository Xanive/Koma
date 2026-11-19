namespace Koma;

/// <summary>Automatic zero-offset numbering is only possible after a complete, unique exact match.</summary>
internal static class AutoTrackingMatch
{
    // The caller must supply a candidate from decision.AutomaticMatch, not merely the top suggestion.
    public static bool CanApprove(DetectedAnime detection, MalAnimeCandidate candidate, bool fromCompleteSearch) =>
        fromCompleteSearch && !candidate.SearchIncomplete && candidate.Id > 0 &&
        detection is { Kind: AnimeKind.Episode, NeedsReview: false, Episode: > 0 } &&
        candidate.EpisodeCount > 0 && detection.Episode <= candidate.EpisodeCount &&
        AnimeMatcher.ExactTitle(detection, candidate) && AnimeMatcher.KindFits(detection, candidate) &&
        AnimeMatcher.SeasonFits(detection, candidate);

    public static bool CanApprove(DetectedAnime detection, AnimeMatchDecision decision) =>
        decision.AutomaticMatch is { } candidate && decision.RankedCandidates.All(item => !item.SearchIncomplete) &&
        CanApprove(detection, candidate, true);
}
