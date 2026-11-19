using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Koma;

public partial class MainWindow
{
    private readonly MalAccountService malAccount;
    private readonly CancellationTokenSource trackingLifetime = new();
    private CancellationTokenSource trackingWrites = new();
    private CancellationTokenSource? accountRequest;
    private readonly Dictionary<int, MalListEntry> accountEntries = [];
    private readonly Dictionary<int, string> trackingNotes = [];
    private readonly HashSet<int> trackingBlocked = [];
    private readonly SemaphoreSlim trackingSyncGate = new(1);
    private MalAccountProfile? trackingProfile;
    private int accountGeneration;
    private int? trackingNextOffset;
    private bool accountBusy, trackingReady;
    private bool trackingSyncRequested;
    private DateTimeOffset nextTrackingRequest, trackingRetryAfter;
    private string trackingMessage = "Connect to see your MyAnimeList list. Local playback works offline.";
    private WatchCoverage? watchCoverage;
    private Episode? coverageEpisode;
    private long coverageLength, coverageModified;

    private sealed record LocalTracked(Episode Episode, Collection? Collection, Season? Season,
        DetectedAnime Detection, string LinkKey, AnimeLink? Link);
    private sealed record TrackingRow(string Title, string Summary, string Note, double Percent,
        LocalTracked[] Local, MalListEntry? Remote, bool NeedsReview)
    { public bool HasLocal => Local.Length > 0; }
    private sealed record TrackingFilterChoice(string Key, string Label);

    private void InitializeTracking()
    {
        trackingReady = true;
        RenderTrackingFilters();
        RenderTracking();
    }

    private void RenderTrackingFilters()
    {
        string selected = (TrackingFilter.SelectedItem as TrackingFilterChoice)?.Key ?? "all";
        TrackingFilter.DisplayMemberPath = nameof(TrackingFilterChoice.Label);
        TrackingFilter.ItemsSource = new[] { ("all", "All anime"), ("local", "On this PC"),
            ("watching", "Watching"), ("completed", "Completed"), ("plan_to_watch", "Plan to watch"),
            ("review", "Needs review") }.Select(x => new TrackingFilterChoice(x.Item1, UiText.Translate(x.Item2))).ToArray();
        TrackingFilter.SelectedItem = TrackingFilter.Items.Cast<TrackingFilterChoice>().First(x => x.Key == selected);
    }

    private void ShowTracking(object sender, RoutedEventArgs e)
    {
        DownloadsPane.Visibility = Visibility.Collapsed;
        HomePane.Visibility = PlayerPane.Visibility = Visibility.Collapsed;
        TrackingPane.Visibility = Visibility.Visible;
        Remember();
        RenderTracking(); UpdateShell();
        if (malAccount.IsConnected && trackingProfile is null && !accountBusy) _ = RefreshTrackingAccount(false);
    }

    private IEnumerable<LocalTracked> LocalTrackedEpisodes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in scanned.Where(x => x.Available).SelectMany(x => x.Collections))
            foreach (var season in collection.Seasons)
                foreach (var episode in season.Episodes)
                    if (seen.Add(episode.Key)) yield return DescribeTracking(episode, collection, season);
        if (playing is not null && seen.Add(playing.Key)) yield return DescribeTracking(playing);
    }

    private LocalTracked DescribeTracking(Episode episode, Collection? collection = null, Season? season = null)
    {
        if (collection is null)
            foreach (var item in scanned.SelectMany(x => x.Collections))
                if (item.Seasons.FirstOrDefault(s => s.Episodes.Any(ep => ep.Key == episode.Key)) is { } found)
                { collection = item; season = found; break; }
        var detected = DownloadSources.Detect(episode, Settings, collection?.IsLoose == true ? null : collection?.Name, season?.Name);
        string key = AnimeLinkKeys.For(episode, detected);
        if (key.Length == 0 && episode.Key.Length < 3500 && !episode.Key.Any(char.IsControl)) key = "episode:" + episode.Key + "|manual";
        return new(episode, collection, season, detected, key, Settings.AnimeLinks.GetValueOrDefault(key));
    }

    private void RenderTracking()
    {
        if (!trackingReady || closing) return;
        bool connected = malAccount.IsConnected;
        TrackingAccount.Text = trackingProfile is { } profile ? UiText.Translate("Connected as {0}", profile.Name) : "MyAnimeList";
        TrackingConnection.Text = UiText.Translate(connected ? "Your account connection is protected on this Windows profile." :
            "Connect to see your MyAnimeList list. Local playback works offline.");
        TrackingConnect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        TrackingConnect.IsEnabled = !accountBusy;
        TrackingConnect.Content = UiText.Translate(accountBusy ? "Waiting for sign-in…" : "Connect MyAnimeList");
        TrackingDisconnect.Visibility = connected || accountBusy ? Visibility.Visible : Visibility.Collapsed;
        TrackingSync.IsEnabled = connected && !accountBusy;
        TrackingAutomatic.IsEnabled = connected && trackingProfile is not null;
        TrackingAutomatic.IsChecked = Settings.AutoTrackAnime;
        TrackingMore.Visibility = trackingNextOffset.HasValue ? Visibility.Visible : Visibility.Collapsed;
        TrackingMore.IsEnabled = !accountBusy;
        TrackingMessage.Text = UiText.Translate(trackingMessage);

        var locals = LocalTrackedEpisodes().ToArray();
        var groups = locals.GroupBy(x => x.Link is { MalId: > 0 } link ? "mal:" + link.MalId : "local:" + x.LinkKey).ToArray();
        var rows = new List<TrackingRow>();
        foreach (var group in groups)
        {
            var items = group.ToArray(); var first = items[0];
            MalListEntry? remote = first.Link is { MalId: > 0 } link ? accountEntries.GetValueOrDefault(link.MalId) : null;
            bool disabled = items.All(x => x.Link?.MalId == 0 || Settings.DisabledTrackingKeys.Contains(x.LinkKey, StringComparer.OrdinalIgnoreCase));
            bool review = !disabled && (items.Any(x => TrackingPolicy.EpisodeNumber(x.Detection, x.Link,
                Settings.TrackingApprovals.GetValueOrDefault(x.LinkKey), trackingProfile?.Id ?? 0) is null) ||
                first.Link is { MalId: > 0 } blocked && trackingBlocked.Contains(blocked.MalId));
            int complete = items.Count(x => ValidCoverage(x.Episode) is { IsComplete: true });
            string title = remote?.Candidate is { EnglishTitle.Length: > 0 } english ? english.EnglishTitle :
                remote?.Title ?? first.Link?.LocalTitle ?? first.Collection?.DisplayName ?? first.Detection.Title;
            if (string.IsNullOrWhiteSpace(title)) title = first.Episode.Name;
            string summary = UiText.Translate("{0} local episodes · {1} watched in Koma", items.Length, complete);
            if (remote is not null) summary += " · " + RemoteProgress(remote);
            string note = disabled ? UiText.Translate("Tracking is off for this show.") : first.Link is { MalId: > 0 } linked && trackingNotes.TryGetValue(linked.MalId, out var status)
                ? UiText.Translate(status) : UiText.Translate(review ? "Confirm the match and episode numbering to track this show." :
                    Settings.AutoTrackAnime ? "Ready for automatic tracking" : "Automatic updates are off");
            rows.Add(new(title, summary, note, remote is { TotalEpisodes: > 0 } ?
                Math.Clamp(remote.Status.WatchedEpisodes * 100d / remote.TotalEpisodes, 0, 100) : complete * 100d / items.Length,
                items, remote, review));
        }
        var localIds = locals.Where(x => x.Link is { MalId: > 0 }).Select(x => x.Link!.MalId).ToHashSet();
        foreach (var entry in accountEntries.Values.Where(x => !localIds.Contains(x.Id)))
            rows.Add(new(entry.Candidate is { EnglishTitle.Length: > 0 } english ? english.EnglishTitle : entry.Title, RemoteProgress(entry), UiText.Translate("No linked local episodes"),
                entry.TotalEpisodes > 0 ? Math.Clamp(entry.Status.WatchedEpisodes * 100d / entry.TotalEpisodes, 0, 100) : 0,
                [], entry, false));
        TrackingLocalCount.Text = groups.Length.ToString(CultureInfo.CurrentCulture);
        TrackingListCount.Text = accountEntries.Count.ToString(CultureInfo.CurrentCulture);
        TrackingReviewCount.Text = rows.Count(x => x.NeedsReview).ToString(CultureInfo.CurrentCulture);
        string filter = (TrackingFilter.SelectedItem as TrackingFilterChoice)?.Key ?? "all";
        string query = TrackingSearch.Text.Trim();
        var visible = rows.Where(x => (query.Length == 0 || x.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            x.Remote?.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true ||
            x.Remote?.Candidate?.AlternativeTitles.Any(name => name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true) &&
            (filter == "all" || filter == "local" && x.HasLocal || filter == "review" && x.NeedsReview || x.Remote?.Status.Status == filter))
            .OrderByDescending(x => x.HasLocal).ThenBy(x => x.Title, Library.Natural).ToArray();
        TrackingRows.ItemsSource = visible;
        if (visible.Length == 0 && !accountBusy) TrackingMessage.Text = UiText.Translate(query.Length > 0 || filter != "all" ?
            "No anime match this filter." : "Your anime will appear here after you add a local folder or connect MyAnimeList.");
    }

    private static string RemoteProgress(MalListEntry entry) => UiText.Translate("MAL: {0} / {1} · {2}",
        entry.Status.WatchedEpisodes, entry.TotalEpisodes > 0 ? entry.TotalEpisodes.ToString(CultureInfo.CurrentCulture) : "?",
        UiText.Translate(entry.Status.Status switch { "watching" => "Watching", "completed" => "Completed", "on_hold" => "On hold",
            "dropped" => "Dropped", "plan_to_watch" => "Plan to watch", _ => "Unknown status" }));
    private void TrackingFilterChanged(object sender, SelectionChangedEventArgs e) => RenderTracking();
    private void TrackingSearchChanged(object sender, TextChangedEventArgs e) => RenderTracking();

    private async void ConnectTracking(object sender, RoutedEventArgs e)
    {
        if (accountBusy) return;
        if (malCredentials.Read() is null) { ConfigureTrackingClient(); if (malCredentials.Read() is null) return; }
        int current = ++accountGeneration;
        accountRequest?.Cancel(); accountRequest = CancellationTokenSource.CreateLinkedTokenSource(trackingLifetime.Token);
        var token = accountRequest.Token;
        accountBusy = true; trackingMessage = "Finish signing in and approving Koma in your browser. You can cancel with Disconnect.";
        RenderTracking();
        try
        {
            await malAccount.ConnectAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), token);
            if (closing || current != accountGeneration) return;
            Settings.AutoTrackAnime = false; SaveSettings();
            accountBusy = false;
            await RefreshTrackingAccount(false);
        }
        catch (OperationCanceledException) { if (current == accountGeneration) trackingMessage = "Sign-in was cancelled or timed out. Local playback still works."; }
        catch (Exception error) when (TrackingError(error))
        { if (current == accountGeneration) trackingMessage = "MyAnimeList could not connect. Check the registered Client ID, callback and browser approval, then try again."; }
        finally { if (current == accountGeneration) { accountBusy = false; RenderTracking(); } }
    }

    private void ConfigureTrackingClient()
    {
        var dialog = TrackingDialog("MyAnimeList Client ID", out var body);
        body.Children.Add(TrackingNote("Enter the Client ID from your registered desktop application. Never enter a client secret."));
        var field = new PasswordBox { Margin = new(0, 14, 0, 14), Padding = new(8) };
        System.Windows.Automation.AutomationProperties.SetName(field, UiText.Translate("MyAnimeList Client ID"));
        body.Children.Add(field);
        var note = TrackingNote(""); body.Children.Add(note);
        var save = new Button { Content = UiText.Translate("Save connection"), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            try {
                if (malCredentials.Read() != field.Password.Trim()) DisconnectTracking(this, new());
                malCredentials.Save(field.Password.Trim()); field.Clear(); dialog.Close();
            }
            catch (Exception error) when (TrackingError(error)) { note.Text = UiText.Translate("The connection could not be saved. Check the Client ID and try again."); }
        };
        body.Children.Add(save); dialog.Closed += (_, _) => field.Clear(); dialog.ShowDialog();
    }

    private void DisconnectTracking(object sender, RoutedEventArgs e)
    {
        playbackIdentification?.Cancel(); knownCandidates.Clear();
        ++accountGeneration; accountRequest?.Cancel();
        Settings.AutoTrackAnime = false; accountBusy = false; trackingProfile = null;
        accountEntries.Clear(); trackingNotes.Clear(); trackingBlocked.Clear(); trackingNextOffset = null;
        try { malAccount.Disconnect(); trackingMessage = "Disconnected. Your local library and progress are still available."; }
        catch (Exception error) when (TrackingError(error)) { trackingMessage = "The saved connection could not be removed."; }
        SaveSettings(); RenderTracking();
    }

    private void TrackingAutomaticChanged(object sender, RoutedEventArgs e)
    {
        Settings.AutoTrackAnime = TrackingAutomatic.IsChecked == true && malAccount.IsConnected && trackingProfile is not null;
        if (!Settings.AutoTrackAnime) playbackIdentification?.Cancel();
        CancelTrackingWrites();
        SaveSettings(); RenderTracking();
        if (Settings.AutoTrackAnime) { _ = IdentifyPlayingAnime(); UpdateRecognition(); _ = SyncCompletedEpisodes(); }
    }
    private async void SyncTracking(object sender, RoutedEventArgs e) => await RefreshTrackingAccount(true);
    private async void LoadMoreTracking(object sender, RoutedEventArgs e)
    { if (trackingNextOffset is { } offset) await FetchTrackingPage(offset); }

    private async Task RefreshTrackingAccount(bool retry)
    {
        if (accountBusy || !malAccount.IsConnected || closing) return;
        int current = accountGeneration;
        accountRequest?.Cancel(); accountRequest = CancellationTokenSource.CreateLinkedTokenSource(trackingLifetime.Token);
        var token = accountRequest.Token;
        accountBusy = true; trackingMessage = "Syncing your MyAnimeList list…"; RenderTracking();
        try
        {
            var profile = await malAccount.GetProfileAsync(token);
            var page = await malAccount.GetListAsync(null, 0, token);
            if (closing || current != accountGeneration || token.IsCancellationRequested) return;
            if (trackingProfile is { } previous && previous.Id != profile.Id)
            { Settings.AutoTrackAnime = false; SaveSettings(); }
            trackingProfile = profile; accountEntries.Clear();
            if (retry) { trackingBlocked.Clear(); trackingNotes.Clear(); }
            foreach (var item in page.Entries) RememberRemoteEntry(item, profile.Id);
            trackingNextOffset = page.NextOffset;
            int pages = 1;
            while (trackingNextOffset is { } offset && pages++ < 20)
            {
                RenderTracking();
                await Task.Delay(1000, token);
                page = await malAccount.GetListAsync(null, offset, token);
                if (closing || current != accountGeneration || token.IsCancellationRequested) return;
                foreach (var item in page.Entries) RememberRemoteEntry(item, profile.Id);
                trackingNextOffset = page.NextOffset;
            }
            trackingMessage = page.NextOffset.HasValue ? "List synced. Load more to see the rest of your anime." : "Your MyAnimeList list is up to date.";
            SaveSettings();
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && current == accountGeneration) trackingMessage = "MyAnimeList is unavailable. Saved local progress is safe; use Sync now to retry."; }
        catch (Exception error) when (TrackingError(error))
        { if (current == accountGeneration) trackingMessage = "MyAnimeList is unavailable. Saved local progress is safe; use Sync now to retry."; }
        finally { if (current == accountGeneration) { accountBusy = false; RenderTracking(); } }
        if (current == accountGeneration && !token.IsCancellationRequested)
        { UpdateRecognition(); await IdentifyPlayingAnime(); await SyncCompletedEpisodes(); }
    }

    private async Task FetchTrackingPage(int offset)
    {
        if (accountBusy || !malAccount.IsConnected || closing) return;
        int current = accountGeneration; accountBusy = true; RenderTracking();
        var token = accountRequest?.Token ?? trackingLifetime.Token;
        try
        {
            var page = await malAccount.GetListAsync(null, offset, token);
            if (current != accountGeneration || closing || token.IsCancellationRequested) return;
            foreach (var item in page.Entries) RememberRemoteEntry(item, trackingProfile?.Id ?? 0);
            trackingNextOffset = page.NextOffset;
            trackingMessage = page.NextOffset.HasValue ? "List synced. Load more to see the rest of your anime." : "Your MyAnimeList list is up to date.";
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && current == accountGeneration) trackingMessage = "MyAnimeList is unavailable. Saved local progress is safe; use Sync now to retry."; }
        catch (Exception error) when (TrackingError(error)) { if (current == accountGeneration) trackingMessage = "MyAnimeList is unavailable. Saved local progress is safe; use Sync now to retry."; }
        finally { if (current == accountGeneration) { accountBusy = false; RenderTracking(); } }
        if (current == accountGeneration && !token.IsCancellationRequested) { UpdateRecognition(); _ = IdentifyPlayingAnime(); }
    }

    private void OpenTrackedShow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrackingRow { Local.Length: > 0 } row }) return;
        OpenTrackingLocal(row.Local[0]);
    }
    private void OpenTrackingLocal(LocalTracked item)
    {
        Collection collection = item.Collection ?? new Collection(item.Detection.Title,
            [new Season(Path.GetFileName(Path.GetDirectoryName(item.Episode.Path)) ?? "", [item.Episode])], IsLoose: true);
        var root = scanned.FirstOrDefault(x => x.Collections.Contains(collection));
        displayingFolders = true; Folders.SelectedItem = root; displayingFolders = false;
        ShowHome(this, new()); DisplayCollection(collection, item.Season?.Name);
        Episodes.SelectedItem = Episodes.Items.Cast<EpisodeRow>().FirstOrDefault(x => x.Episode.Key == item.Episode.Key);
    }

    private void ReviewTrackedShow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrackingRow { Local.Length: > 0 } row }) return;
        var item = row.Local.FirstOrDefault(x => TrackingPolicy.EpisodeNumber(x.Detection, x.Link,
            Settings.TrackingApprovals.GetValueOrDefault(x.LinkKey), trackingProfile?.Id ?? 0) is null) ?? row.Local[0];
        ShowAnimeChooser(new(item.Episode, item.Detection, item.LinkKey));
        RenderTracking();
    }
    private Window TrackingDialog(string title, out StackPanel body)
    {
        var dialog = new Wpf.Ui.Controls.FluentWindow { Owner = this, Title = UiText.Translate(title), Width = 560, Height = 440,
            MinWidth = 380, MinHeight = 300, MaxWidth = Math.Max(380, ActualWidth - 32), MaxHeight = Math.Max(300, ActualHeight - 32),
            ExtendsContentIntoTitleBar = true, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("BackgroundBrush"), Foreground = Foreground,
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None };
        body = new StackPanel { Margin = new(22) };
        var root = new DockPanel();
        var header = new Wpf.Ui.Controls.TitleBar { Title = UiText.Translate(title), Height = 44, ShowMinimize = false, ShowMaximize = false };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var cancel = new Button { Content = UiText.Translate("Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(18, 6, 18, 12) };
        cancel.Click += (_, _) => dialog.Close(); DockPanel.SetDock(cancel, Dock.Bottom); root.Children.Add(cancel);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        return dialog;
    }
    private TextBlock TrackingNote(string text) => new() { Text = UiText.Translate(text), TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("MutedBrush"), Margin = new(0, 10, 0, 4) };

    private void BeginWatchCoverage(Episode episode)
    {
        coverageEpisode = episode;
        try { var file = new FileInfo(episode.Path); coverageLength = file.Length; coverageModified = file.LastWriteTimeUtc.Ticks; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { coverageLength = coverageModified = 0; }
        watchCoverage = new(ValidCoverage(episode));
    }
    private WatchedEpisode? ValidCoverage(Episode episode)
    {
        if (!Settings.WatchedCoverage.TryGetValue(episode.Key, out var saved)) return null;
        try { var file = new FileInfo(episode.Path); return file.Exists && saved.FileLength > 0 && saved.FileLength == file.Length && saved.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks ? saved : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    private void SampleWatchedPlayback()
    {
        if (watchCoverage is null || playing is null || coverageEpisode?.Key != playing.Key || !loaded) return;
        watchCoverage.Sample(position, duration, paused || seeking, speed, clock.Elapsed);
        bool wasComplete = Settings.WatchedCoverage.GetValueOrDefault(playing.Key)?.IsComplete == true;
        var snapshot = watchCoverage.Snapshot() with { FileLength = coverageLength, ModifiedUtcTicks = coverageModified };
        Settings.WatchedCoverage[playing.Key] = snapshot;
        if (!wasComplete && snapshot.IsComplete) { SaveSettings(); RenderTracking(); _ = SyncCompletedEpisodes(); }
    }
    private void SaveWatchCoverage()
    {
        if (watchCoverage is not null && coverageEpisode is not null)
            Settings.WatchedCoverage[coverageEpisode.Key] = watchCoverage.Snapshot() with { FileLength = coverageLength, ModifiedUtcTicks = coverageModified };
    }

    private async Task SyncCompletedEpisodes()
    {
        if (closing || !Settings.AutoTrackAnime || trackingProfile is null || !malAccount.IsConnected || DateTimeOffset.UtcNow < trackingRetryAfter) return;
        trackingSyncRequested = true;
        if (!await trackingSyncGate.WaitAsync(0)) return;
        trackingSyncRequested = false;
        int current = accountGeneration; long owner = trackingProfile.Id;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(accountRequest?.Token ?? trackingLifetime.Token, trackingWrites.Token);
        var token = request.Token;
        try
        {
            var ids = LocalTrackedEpisodes().Where(x => x.Link is { Confirmed: true, MalId: > 0 } &&
                !Settings.DisabledTrackingKeys.Contains(x.LinkKey, StringComparer.OrdinalIgnoreCase) &&
                ValidCoverage(x.Episode) is { IsComplete: true, Duration: >= 60 } &&
                TrackingPolicy.EpisodeNumber(x.Detection, x.Link, Settings.TrackingApprovals.GetValueOrDefault(x.LinkKey), owner) is { } number &&
                number > Settings.TrackingReceipts.GetValueOrDefault(owner + "/" + x.Link.MalId)).Select(x => x.Link!.MalId).Distinct().ToArray();
            foreach (int id in ids)
            {
                if (closing || current != accountGeneration || !Settings.AutoTrackAnime || !malAccount.IsConnected || token.IsCancellationRequested) break;
                if (trackingBlocked.Contains(id)) continue;
                try
                {
                    TimeSpan delay = nextTrackingRequest - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    nextTrackingRequest = DateTimeOffset.UtcNow.AddSeconds(1);
                    var remote = await malAccount.GetStatusAsync(id, token);
                    if (current != accountGeneration || closing || !Settings.AutoTrackAnime || token.IsCancellationRequested) break;
                    string receiptKey = owner + "/" + id;
                    int previous = remote?.WatchedEpisodes ?? 0;
                    if (remote is null && Settings.TrackingReceipts.ContainsKey(receiptKey) ||
                        previous < Settings.TrackingReceipts.GetValueOrDefault(receiptKey) || remote is { IsRewatching: true } || remote?.Status is "on_hold" or "dropped")
                    { trackingNotes[id] = "Your MAL list changed or tracking is paused. Review it before retrying."; trackingBlocked.Add(id); continue; }
                    if (remote is not null) Settings.TrackingReceipts[receiptKey] = previous;
                    var eligible = LocalTrackedEpisodes().Where(x => x.Link?.MalId == id && ValidCoverage(x.Episode) is { IsComplete: true, Duration: >= 60 })
                        .Select(x => TrackingPolicy.EpisodeNumber(x.Detection, x.Link, Settings.TrackingApprovals.GetValueOrDefault(x.LinkKey), owner))
                        .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                    int total = remote?.TotalEpisodes ?? accountEntries.GetValueOrDefault(id)?.TotalEpisodes ?? 0;
                    int next = WatchTracking.ContiguousEpisodeProgress(previous, eligible, total > 0 ? total : null);
                    if (next <= previous)
                    {
                        trackingNotes[id] = eligible.Any(x => x > previous + 1) ? "An earlier episode is missing. Koma will not skip your MAL progress." :
                            eligible.Length == 0 ? "Confirm the match and episode numbering to track this show." : "Progress already synced";
                        continue;
                    }
                    if (current != accountGeneration || !Settings.AutoTrackAnime || token.IsCancellationRequested) break;
                    var updated = await malAccount.UpdateProgressAsync(id, next, previous, token, expectedExists: remote is not null);
                    if (current != accountGeneration || closing) break;
                    Settings.TrackingReceipts[receiptKey] = updated.WatchedEpisodes;
                    trackingNotes[id] = "Progress synced to MyAnimeList";
                    if (accountEntries.TryGetValue(id, out var entry)) accountEntries[id] = entry with { Status = updated };
                    else
                    {
                        string title = LocalTrackedEpisodes().First(x => x.Link?.MalId == id).Link!.LocalTitle;
                        accountEntries[id] = new(id, title, total, updated);
                    }
                    SaveSettings();
                }
                catch (OperationCanceledException)
                {
                    if (!token.IsCancellationRequested && current == accountGeneration)
                    { trackingNotes[id] = "Update needs review. Use Sync now to check MAL before retrying."; trackingBlocked.Add(id); }
                    break;
                }
                catch (Exception error) when (TrackingError(error))
                {
                    if (current != accountGeneration || closing) break;
                    trackingNotes[id] = "Update needs review. Use Sync now to check MAL before retrying."; trackingBlocked.Add(id);
                    if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                    { trackingRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1); break; }
                }
            }
        }
        finally
        {
            trackingSyncGate.Release();
            if (!closing && current == accountGeneration) { SaveSettings(); RenderTracking(); }
            if (trackingSyncRequested && !closing) _ = SyncCompletedEpisodes();
        }
    }

    private static bool TrackingError(Exception error) => error is HttpRequestException or IOException or UnauthorizedAccessException or
        CryptographicException or InvalidOperationException or ArgumentException or TimeoutException or Win32Exception or System.Text.Json.JsonException;

    private void CancelTrackingWrites()
    {
        var previous = trackingWrites; trackingWrites = new(); previous.Cancel(); previous.Dispose();
    }

    private void RememberRemoteEntry(MalListEntry item, long owner)
    {
        accountEntries[item.Id] = item;
        if (item.Candidate is { } candidate) knownCandidates[item.Id] = candidate;
        if (owner > 0)
        {
            string key = owner + "/" + item.Id;
            if (item.Status.WatchedEpisodes < Settings.TrackingReceipts.GetValueOrDefault(key))
            { trackingNotes[item.Id] = "Your MAL list changed or tracking is paused. Review it before retrying."; trackingBlocked.Add(item.Id); }
            Settings.TrackingReceipts[key] = Math.Max(Settings.TrackingReceipts.GetValueOrDefault(key), item.Status.WatchedEpisodes);
        }
    }
}
