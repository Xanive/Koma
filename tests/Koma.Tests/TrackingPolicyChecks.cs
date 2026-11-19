using Koma;

internal static class TrackingPolicyChecks
{
    public static void Run(string scratch)
    {
        var now = DateTimeOffset.UtcNow;
        var detected = new DetectedAnime("Example", 2, 14, AnimeKind.Episode, false);
        var link = new AnimeLink(123, true, "Example", now);
        var approval = new TrackingApproval(123, 456, -13, now);
        Require(TrackingPolicy.EpisodeNumber(detected, link, approval, 456) == 1, "An approved absolute-numbering offset should map to MAL episode 1.");
        Require(TrackingPolicy.EpisodeNumber(detected, link, approval, 789) is null, "Approval cannot transfer to another account.");
        Require(TrackingPolicy.EpisodeNumber(detected, link with { Confirmed = false }, approval, 456) is null, "Automatic catalogue matches are not write approval.");
        Require(TrackingPolicy.EpisodeNumber(detected, link with { Updated = now.AddSeconds(1) }, approval, 456) is null, "Changing the mapping must invalidate prior numbering consent.");
        Require(TrackingPolicy.EpisodeNumber(detected, link with { MalId = 789 }, approval, 456) is null, "A different catalogue entry needs new approval.");
        Require(TrackingPolicy.EpisodeNumber(detected with { NeedsReview = true }, link, approval, 456) is null, "Ambiguous episode hints must not sync.");
        Require(TrackingPolicy.EpisodeNumber(detected with { NeedsReview = true }, link,
            approval with { ReviewedUncertainHints = true }, 456) == 1, "A user's explicit number review can resolve conflicting hints without guessing.");
        Require(TrackingPolicy.EpisodeNumber(detected with { Kind = AnimeKind.Special }, link, approval, 456) is null, "Specials require a separate workflow.");
        Require(TrackingPolicy.EpisodeNumber(detected, link, approval with { EpisodeOffset = -14 }, 456) is null, "Nonpositive resulting episode numbers must not sync.");

        var store = new PreferenceStore(scratch, Path.Combine(scratch, "missing-legacy"));
        Require(!store.Data.AutoTrackAnime && store.Data.WatchedCoverage.Count == 0, "Online account updates must default off.");
        store.Data.TrackingApprovals["season-key"] = approval;
        store.Data.TrackingApprovals["bad"] = approval with { AccountId = 0 };
        store.Data.WatchedCoverage["episode-key"] = new(90, [new(0, 75)], now, 12345, now.UtcTicks);
        store.Data.TrackingReceipts["456/123"] = 4;
        store.Data.TrackingReceipts["invalid-account/path"] = 999;
        store.Data.AutoTrackAnime = true;
        store.Save();
        var restored = new PreferenceStore(scratch, Path.Combine(scratch, "missing-legacy"));
        Require(restored.Data.AutoTrackAnime && restored.Data.TrackingApprovals.Count == 1 &&
            restored.Data.TrackingApprovals["season-key"] == approval, "Numbering consent should round-trip and reject malformed records.");
        Require(restored.Data.WatchedCoverage["episode-key"] is { IsComplete: true, FileLength: 12345 } &&
            restored.Data.WatchedCoverage["episode-key"].ModifiedUtcTicks == now.UtcTicks, "Viewing evidence and its file stamp must survive resume.");
        Require(restored.Data.TrackingReceipts.Count == 1 && restored.Data.TrackingReceipts["456/123"] == 4,
            "Observed remote counts should survive restarts without malformed keys.");
    }

    private static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
}
