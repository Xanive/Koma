using System.Net.Http;
using System.Windows;

namespace Koma;

public partial class MainWindow
{
    private readonly MatchCoverCache matchCovers = new();
    private readonly Dictionary<int, MalAnimeCandidate> knownCandidates = [];
    private CancellationTokenSource? playbackIdentification;
    private string? identifyingEpisode;
    private double nextTrackingLabel;
    private bool AutomaticLookupAllowed => Settings.OnlineAnimeRecognition || Settings.AutoTrackAnime && malAccount.IsConnected;

    private RecognitionContext RecognitionFor(Episode episode)
    {
        var local = DescribeTracking(episode);
        return new(local.Episode, local.Detection, local.LinkKey);
    }

    private AnimeMatchDecision MatchMyList(RecognitionContext context)
    {
        bool incomplete = accountBusy || trackingNextOffset.HasValue;
        return AnimeMatcher.Rank(context.Detection, accountEntries.Values.Where(x => x.Candidate is not null)
            .Select(x => incomplete ? x.Candidate! with { SearchIncomplete = true } : x.Candidate!));
    }

    private bool ApplyAutomaticMatch(RecognitionContext context, AnimeMatchDecision decision)
    {
        if (context.Key.Length == 0 || decision.AutomaticMatch is not { } candidate) return false;
        if (Settings.AnimeLinks.TryGetValue(context.Key, out var existing) &&
            (existing.MalId == 0 || existing.Confirmed && existing.MalId != candidate.Id)) return false;
        bool approve = Settings.AutoTrackAnime && trackingProfile is not null && malAccount.IsConnected &&
            !Settings.DisabledTrackingKeys.Contains(context.Key, StringComparer.OrdinalIgnoreCase) &&
            AutoTrackingMatch.CanApprove(context.Detection, decision);
        knownCandidates[candidate.Id] = candidate;
        if (existing is null || existing.MalId != candidate.Id || approve && !existing.Confirmed)
        {
            RememberAnimeLink(context, candidate.Id, approve, candidate.EnglishTitle.Length > 0 ? candidate.EnglishTitle : candidate.Title);
            existing = Settings.AnimeLinks[context.Key];
        }
        if (approve && TrackingPolicy.EpisodeNumber(context.Detection, existing,
            Settings.TrackingApprovals.GetValueOrDefault(context.Key), trackingProfile!.Id) is null)
        {
            Settings.TrackingApprovals[context.Key] = new(candidate.Id, trackingProfile!.Id, 0, existing.Updated);
            SaveSettings();
        }
        return true;
    }

    private async Task IdentifyPlayingAnime()
    {
        if (closing || playing is null || !AutomaticLookupAllowed) { UpdatePlaybackTracking(); return; }
        if (Settings.AutoTrackAnime && malAccount.IsConnected && (trackingProfile is null || accountBusy))
        { UpdatePlaybackTracking(); return; }
        var episode = playing; int epoch = generation;
        var context = RecognitionFor(episode);
        if (Settings.DisabledTrackingKeys.Contains(context.Key, StringComparer.OrdinalIgnoreCase)) { UpdatePlaybackTracking(); return; }
        if (Settings.AnimeLinks.TryGetValue(context.Key, out var before) && (before.MalId == 0 ||
            TrackingPolicy.EpisodeNumber(context.Detection, before, Settings.TrackingApprovals.GetValueOrDefault(context.Key), trackingProfile?.Id ?? 0) is not null))
        { UpdatePlaybackTracking(); return; }
        if (identifyingEpisode == episode.Key) return;
        if (ApplyAutomaticMatch(context, MatchMyList(context)))
        { UpdateRecognition(); UpdatePlaybackTracking(); RenderTracking(); _ = SyncCompletedEpisodes(); return; }
        if (context.Detection.NeedsReview || string.IsNullOrWhiteSpace(context.Detection.Title)) { UpdatePlaybackTracking(); return; }
        playbackIdentification?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(trackingLifetime.Token);
        playbackIdentification = request; identifyingEpisode = episode.Key; UpdatePlaybackTracking();
        try
        {
            var candidates = await SearchAnime(context.Detection.Title, request.Token);
            if (closing || request.IsCancellationRequested || epoch != generation || playing?.Key != episode.Key || !AutomaticLookupAllowed) return;
            // A selection made while the request was pending always wins.
            if (Settings.AnimeLinks.TryGetValue(context.Key, out var current) && current != before) return;
            foreach (var candidate in candidates) knownCandidates[candidate.Id] = candidate;
            ApplyAutomaticMatch(context, AnimeMatcher.Rank(context.Detection, candidates));
            UpdateRecognition(); RenderTracking(); _ = SyncCompletedEpisodes();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or ArgumentException or InvalidOperationException) { }
        finally
        {
            if (playbackIdentification == request) { playbackIdentification = null; identifyingEpisode = null; }
            UpdatePlaybackTracking();
        }
    }

    private void ReviewPlaybackMatch(object sender, RoutedEventArgs e)
    {
        if (playing is not null) ShowAnimeChooser(RecognitionFor(playing));
    }

    private void UpdatePlaybackTracking()
    {
        if (PlaybackTrackingBanner is null || store is null || closing) return;
        PlaybackTrackingBanner.Visibility = !fullscreen && playing is not null && (malAccount.IsConnected || AutomaticLookupAllowed)
            ? Visibility.Visible : Visibility.Collapsed;
        if (playing is null) return;
        var context = RecognitionFor(playing);
        var link = Settings.AnimeLinks.GetValueOrDefault(context.Key);
        string label;
        if (Settings.DisabledTrackingKeys.Contains(context.Key, StringComparer.OrdinalIgnoreCase) || link?.MalId == 0)
            label = UiText.Translate("Tracking is off for this show.");
        else if (!malAccount.IsConnected) label = UiText.Translate("Connect MyAnimeList to track this episode.");
        else if (!Settings.AutoTrackAnime) label = UiText.Translate("Automatic updates are off");
        else if (accountBusy) label = UiText.Translate("Loading your MyAnimeList list…");
        else if (trackingProfile is null) label = UiText.Translate(trackingMessage);
        else if (identifyingEpisode == playing.Key) label = UiText.Translate("Identifying this anime…");
        else if (link is null) label = UiText.Translate("Choose the anime once to enable tracking.");
        else if (TrackingPolicy.EpisodeNumber(context.Detection, link,
            Settings.TrackingApprovals.GetValueOrDefault(context.Key), trackingProfile.Id) is not { } episode)
            label = UiText.Translate("Confirm the anime and episode number once.");
        else
        {
            string title = knownCandidates.GetValueOrDefault(link.MalId) is { EnglishTitle.Length: > 0 } candidate ? candidate.EnglishTitle : link.LocalTitle;
            if (trackingBlocked.Contains(link.MalId) && trackingNotes.TryGetValue(link.MalId, out string? problem)) label = title + " · " + UiText.Translate(problem);
            else if (Settings.TrackingReceipts.GetValueOrDefault(trackingProfile.Id + "/" + link.MalId) >= episode)
                label = UiText.Translate("{0} · Episode {1} already recorded on MAL", title, episode);
            else
            {
                double percent = watchCoverage?.Snapshot().Fraction * 100 ?? ValidCoverage(playing)?.Fraction * 100 ?? 0;
                label = percent >= 80 && trackingNotes.TryGetValue(link.MalId, out string? note) ? title + " · " + UiText.Translate(note) :
                    UiText.Translate("{0} · Episode {1} · {2:0}% watched · updates at 80%", title, episode, percent);
            }
        }
        PlaybackTrackingStatus.Text = label; PlaybackTrackingStatus.ToolTip = label;
    }
}
