namespace Koma;

// Approval belongs to one account, local season and exact remembered catalogue choice.
internal sealed record TrackingApproval(int MalId, long AccountId, int EpisodeOffset, DateTimeOffset LinkUpdated)
{
    public bool ReviewedUncertainHints { get; init; }
}

internal static class TrackingPolicy
{
    public static Dictionary<string, TrackingApproval> Normalize(Dictionary<string, TrackingApproval>? values)
    {
        var result = new Dictionary<string, TrackingApproval>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in (values ?? []).Take(10000))
            if (!string.IsNullOrWhiteSpace(key) && key.Length <= 4000 && !key.Any(char.IsControl) &&
                value is { MalId: > 0, AccountId: > 0, EpisodeOffset: >= -9999 and <= 9999 }) result[key] = value;
        return result;
    }

    public static int? EpisodeNumber(DetectedAnime detected, AnimeLink? link, TrackingApproval? approval, long accountId)
    {
        if (detected is not { Kind: AnimeKind.Episode, Episode: > 0 } ||
            link is not { Confirmed: true, MalId: > 0 } || approval is null ||
            approval.AccountId != accountId || approval.MalId != link.MalId || approval.LinkUpdated != link.Updated ||
            detected.NeedsReview && !approval.ReviewedUncertainHints) return null;
        int number = detected.Episode.Value + approval.EpisodeOffset;
        return number is > 0 and <= 10000 ? number : null;
    }
}
