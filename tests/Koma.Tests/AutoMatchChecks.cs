using Koma;

internal static class AutoMatchChecks
{
    public static void Run(string scratch)
    {
        var episode = new DetectedAnime("Frieren", 1, 7, AnimeKind.Episode, false);
        var tv = new MalAnimeCandidate(1, "Sousou no Frieren", [], 2023, "tv", 28) { EnglishTitle = "Frieren" };
        var decision = AnimeMatcher.Rank(episode, [tv]);
        Require(decision.AutomaticMatch?.Id == 1 && decision.RecommendedMatch?.Id == 1 && AutoTrackingMatch.CanApprove(episode, decision),
            "A complete unique English alias and normal numbered episode should be ready automatically.");
        var movie = tv with { Id = 2, MediaType = "movie", EpisodeCount = 1 };
        Require(AnimeMatcher.Rank(episode, [movie, tv]).AutomaticMatch?.Id == 1,
            "A movie with the same alias must not block a clear numbered television match.");
        var remake = tv with { Id = 3, StartYear = 2040, EpisodeCount = 6 };
        var remakes = AnimeMatcher.Rank(episode, [tv, remake]);
        Require(remakes.AutomaticMatch is null && remakes.RecommendedMatch?.Id == 1 && !AutoTrackingMatch.CanApprove(episode, remakes),
            "Episode counts may recommend a choice, but cannot silently resolve two exact television remakes.");
        Require(AnimeMatcher.Rank(episode with { Episode = 1 }, [tv, remake]).RecommendedMatch is null,
            "Equal remake choices must not receive a misleading recommendation.");
        var fuzzy = episode with { Title = "Frieren Beyond" };
        var close = tv with { EnglishTitle = "Frieren Beyond Journey End" };
        var fuzzyDecision = AnimeMatcher.Rank(fuzzy, [close]);
        Require(fuzzyDecision.RecommendedMatch?.Id == 1 && fuzzyDecision.AutomaticMatch is null &&
            !AutoTrackingMatch.CanApprove(fuzzy, close, true), "Fuzzy suggestions are useful choices, not permission to update a list.");
        var second = tv with { Title = "Sousou no Frieren Season 2", EnglishTitle = "Frieren Season 2", AlternativeTitles = ["Frieren"], EpisodeCount = 12 };
        Require(AnimeMatcher.Rank(episode, [second]).AutomaticMatch is null,
            "A generic alias and first-season filename must not become a second-season entry.");
        var season2 = episode with { Title = "Frieren Season 2", Season = 2, Episode = 3 };
        Require(AutoTrackingMatch.CanApprove(season2, AnimeMatcher.Rank(season2, [second])),
            "An exact explicitly agreeing second season can use normal numbering.");
        Require(!AutoTrackingMatch.CanApprove(season2, second with { EnglishTitle = "Frieren", Title = "Sousou no Frieren" }, true),
            "A season-two folder needs explicit provider season agreement.");
        foreach (var unsafeEpisode in new[] { episode with { Episode = 29 }, episode with { Episode = null },
            episode with { NeedsReview = true }, episode with { Kind = AnimeKind.Special }, episode with { Season = 0 } })
            Require(!AutoTrackingMatch.CanApprove(unsafeEpisode, tv, true), "Uncertain, absolute out-of-range or special numbering must stay under review.");
        Require(!AutoTrackingMatch.CanApprove(episode, tv with { EpisodeCount = 0 }, true),
            "Unknown episode totals do not justify automatic numbering approval.");
        Require(!AutoTrackingMatch.CanApprove(episode, tv, false) && !AutoTrackingMatch.CanApprove(episode, tv with { SearchIncomplete = true }, true),
            "A partial list or incomplete catalogue search cannot establish uniqueness.");
        Require(AnimeMatcher.MatchReason(episode, tv) == "Exact title match" && AnimeMatcher.MatchReason(fuzzy, close) == "Closest title",
            "Review cards should explain exact versus suggested choices without claiming invented confidence.");
    }

    private static void Require(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
}
