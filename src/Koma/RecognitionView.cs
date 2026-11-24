using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Koma;

public partial class MainWindow
{
    private readonly MalCredentialStore malCredentials;
    private readonly MalAppRegistration? malRegistration;
    private string? MalClientId => malCredentials.Read() ?? malRegistration?.ClientId;
    private HttpClient? recognitionHttp;
    private CancellationTokenSource? recognitionRequest;
    private readonly SemaphoreSlim recognitionGate = new(1);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<MalAnimeCandidate> Items)> recognitionCache = new();
    private readonly Dictionary<string, DateTimeOffset> recognitionFailures = new();
    private DateTimeOffset lastRecognitionSearch;
    private DateTimeOffset recognitionRetryAfter;
    private string? recognitionInFlight;
    private RecognitionContext? recognitionContext;

    private sealed record RecognitionContext(Episode Episode, DetectedAnime Detection, string Key);

    private RecognitionContext? CurrentRecognition()
    {
        Episode? episode = (Episodes.SelectedItem as EpisodeRow)?.Episode ?? displayedSeason?.Episodes.FirstOrDefault();
        if (episode is null || selectedCollection is null) return null;
        var detection = DownloadSources.Detect(episode, Settings,
            selectedCollection.IsLoose ? null : selectedCollection.Name, displayedSeason?.Name);
        string key = AnimeLinkKeys.For(episode, detection);
        if (key.Length == 0 && episode.Key.Length < 3500 && !episode.Key.Any(char.IsControl))
            key = "episode:" + episode.Key + "|manual";
        return new(episode, detection, key);
    }

    private void RecognitionEpisodeChanged(object sender, SelectionChangedEventArgs e) => UpdateRecognition();

    private string DetectionLabel(DetectedAnime detection)
    {
        string title = string.IsNullOrWhiteSpace(detection.Title) ? UiText.Translate("Title needs review") : detection.Title;
        if (detection.Season is { } season) title += " · " + UiText.Translate("Season {0}", season);
        if (detection.Episode is { } episode) title += " · " + UiText.Translate("Episode {0}", episode);
        return title;
    }

    private void UpdateRecognition()
    {
        if (RecognitionPanel is null || store is null || closing) return;
        RecognitionContext? context = CurrentRecognition();
        recognitionContext = context;
        RecognitionPanel.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        if (context is null)
        {
            recognitionRequest?.Cancel();
            recognitionInFlight = null;
            return;
        }
        if (recognitionInFlight is not null && recognitionInFlight != context.Key)
        {
            recognitionRequest?.Cancel();
            recognitionInFlight = null;
        }
        if (AutomaticLookupAllowed) ApplyAutomaticMatch(context, MatchMyList(context));
        DetectedTitle.Text = UiText.Translate("Detected: {0}", DetectionLabel(context.Detection));
        if (Settings.AnimeLinks.TryGetValue(context.Key, out var link))
        {
            if (string.IsNullOrWhiteSpace(context.Detection.Title))
                DetectedTitle.Text = UiText.Translate("Detected: {0}", link.LocalTitle);
            RecognitionStatus.Text = link.MalId == 0 ? UiText.Translate("Automatic matching is off for this title.") :
                UiText.Translate(link.Confirmed ? "MyAnimeList #{0} · confirmed match" : "MyAnimeList #{0} · automatic match", link.MalId);
            return;
        }
        RecognitionStatus.Text = context.Detection.NeedsReview ? UiText.Translate("Review the title and season before linking.") :
            UiText.Translate("Recognized locally · not linked to MyAnimeList");
        if (context.Key.Length > 0 && !context.Detection.NeedsReview && AutomaticLookupAllowed &&
            !Settings.DisabledTrackingKeys.Contains(context.Key, StringComparer.OrdinalIgnoreCase) && MalClientId is not null)
            _ = MatchCurrentAnime(context);
    }

    private async Task MatchCurrentAnime(RecognitionContext context)
    {
        if (recognitionInFlight == context.Key) return;
        recognitionRequest?.Cancel();
        using var request = new CancellationTokenSource();
        recognitionRequest = request;
        recognitionInFlight = context.Key;
        RecognitionStatus.Text = UiText.Translate("Looking for a MyAnimeList match…");
        try
        {
            var candidates = await SearchAnime(context.Detection.Title, request.Token);
            if (closing || request.IsCancellationRequested || recognitionContext?.Key != context.Key ||
                recognitionRequest != request || !AutomaticLookupAllowed) return;
            // A user decision always wins over a result that arrived later.
            if (Settings.AnimeLinks.ContainsKey(context.Key)) { UpdateRecognition(); return; }
            var current = recognitionContext;
            var decision = AnimeMatcher.Rank(current.Detection, candidates);
            if (decision.AutomaticMatch is { } match)
            {
                ApplyAutomaticMatch(current, decision);
                RecognitionStatus.Text = UiText.Translate("MyAnimeList #{0} · automatic match", match.Id);
            }
            else RecognitionStatus.Text = UiText.Translate(candidates.Count == 0 ?
                "No match found. Review the detected title." : "Several possibilities · choose a match.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            if (!closing && recognitionContext?.Key == context.Key && recognitionRequest == request)
                RecognitionStatus.Text = UiText.Translate("MyAnimeList is unavailable. Local playback still works.");
        }
        finally
        {
            if (recognitionRequest == request) { recognitionRequest = null; recognitionInFlight = null; }
        }
    }

    private void RememberAnimeLink(RecognitionContext context, int id, bool confirmed, string? localTitle = null)
    {
        if (context.Key.Length == 0) return;
        CancelTrackingWrites();
        Settings.TrackingApprovals.Remove(context.Key);
        string title = string.IsNullOrWhiteSpace(localTitle) ? context.Detection.Title : localTitle.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = context.Episode.Name;
        Settings.AnimeLinks[context.Key] = new(id, confirmed, title, DateTimeOffset.UtcNow);
        SaveSettings();
    }

    private async Task<IReadOnlyList<MalAnimeCandidate>> SearchAnime(string title, CancellationToken token, bool explicitSearch = false)
    {
        if (!AutomaticLookupAllowed && !explicitSearch) throw new InvalidOperationException();
        string key = AnimeRecognition.NormalizeTitle(title);
        await recognitionGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!AutomaticLookupAllowed && !explicitSearch) throw new OperationCanceledException(token);
            if (DateTimeOffset.UtcNow < recognitionRetryAfter ||
                recognitionFailures.TryGetValue(key, out var failedUntil) && DateTimeOffset.UtcNow < failedUntil)
                throw new HttpRequestException("Wait before trying MyAnimeList again.");
            if (recognitionCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5))
                return cached.Items;
            string clientId = MalClientId ?? throw new InvalidOperationException();
            TimeSpan delay = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - lastRecognitionSearch);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            token.ThrowIfCancellationRequested();
            if (!AutomaticLookupAllowed && !explicitSearch) throw new OperationCanceledException(token);
            recognitionHttp ??= MalSearchClient.CreateHttpClient();
            lastRecognitionSearch = DateTimeOffset.UtcNow;
            IReadOnlyList<MalAnimeCandidate> results;
            try { results = await new MalSearchClient(recognitionHttp, clientId).SearchAsync(title, token); }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                if (recognitionFailures.Count >= 64) recognitionFailures.Clear();
                recognitionFailures[key] = DateTimeOffset.UtcNow.AddSeconds(30);
                if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                    recognitionRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
                throw;
            }
            token.ThrowIfCancellationRequested();
            if (!AutomaticLookupAllowed && !explicitSearch) throw new OperationCanceledException(token);
            if (recognitionCache.Count >= 64) recognitionCache.Clear();
            recognitionCache[key] = (DateTimeOffset.UtcNow, results);
            return results;
        }
        finally { recognitionGate.Release(); }
    }

    private void ReviewAnimeMatch(object sender, RoutedEventArgs e)
    {
        if (CurrentRecognition() is { } context) ShowAnimeChooser(context);
    }
}
