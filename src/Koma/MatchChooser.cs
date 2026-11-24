using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Koma;

public partial class MainWindow
{
    private void ShowAnimeChooser(RecognitionContext context)
    {
        if (context.Key.Length == 0) return;
        var dialog = new Wpf.Ui.Controls.FluentWindow {
            Owner = this, Title = UiText.Translate("Choose anime"), Width = Math.Min(800, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 60), MinWidth = 470, MinHeight = 430,
            ExtendsContentIntoTitleBar = true, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BackgroundBrush"), Foreground = Foreground, FontSize = 14
        };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(trackingLifetime.Token);
        CancellationTokenSource? searching = null;
        int accountEpoch = accountGeneration;
        bool alive = true, configure = false;
        var root = new DockPanel { Background = (Brush)FindResource("BackgroundBrush") };
        var header = new Wpf.Ui.Controls.TitleBar { Title = UiText.Translate("Choose anime"), Height = 44, ShowMinimize = false, ShowMaximize = false };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var intro = new StackPanel { Margin = new(22, 12, 22, 12) };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        intro.Children.Add(new TextBlock { Text = DetectionLabel(context.Detection), FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var introHelp = ChooserNote("Your list comes first. Check the cover, year and season, then choose once."); intro.Children.Add(introHelp);
        var searchRow = new DockPanel { Margin = new(0, 10, 0, 0) };
        var search = new Button { Content = UiText.Translate("Search MyAnimeList"), Margin = new(10, 0, 0, 0) };
        DockPanel.SetDock(search, Dock.Right); searchRow.Children.Add(search);
        var query = new Wpf.Ui.Controls.TextBox { Text = context.Detection.Title, MaxLength = 200, PlaceholderText = UiText.Translate("Anime title"), Padding = new(9, 7, 9, 7) };
        System.Windows.Automation.AutomationProperties.SetName(query, UiText.Translate("Anime title")); searchRow.Children.Add(query); intro.Children.Add(searchRow);
        var status = ChooserNote("Choose the matching show from your list, or search the catalogue."); intro.Children.Add(status);
        var results = new ListBox { Margin = new(22, 0, 22, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top };
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(results, UiText.Translate("Possible anime matches"));

        var footer = new StackPanel { Margin = new(22, 12, 22, 18) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var selection = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        footer.Children.Add(selection);
        var numberRow = new DockPanel { Margin = new(0, 10, 0, 4) };
        var number = new TextBox { Width = 76, MaxLength = 5, Padding = new(8), HorizontalContentAlignment = HorizontalAlignment.Center,
            Text = context.Detection.Episode?.ToString(CultureInfo.InvariantCulture) ?? "" };
        System.Windows.Automation.AutomationProperties.SetName(number, UiText.Translate("MyAnimeList episode number"));
        DockPanel.SetDock(number, Dock.Right); numberRow.Children.Add(number);
        numberRow.Children.Add(new TextBlock { Text = UiText.Translate("Episode number on MyAnimeList"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 10, 0) });
        footer.Children.Add(numberRow);
        var numberHelp = ChooserNote("The suggested number is filled in. Change it for split seasons or absolute numbering."); footer.Children.Add(numberHelp);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var connection = new Button { Content = UiText.Translate(MalClientId is null ? "Advanced connection" : "Connect MyAnimeList") };
        var ignore = new Button { Content = UiText.Translate("Disable for this show") };
        var use = new Button { Content = UiText.Translate("Use this match"), IsEnabled = false, Style = (Style)FindResource("PrimaryButton") };
        buttons.Children.Add(connection); buttons.Children.Add(ignore); buttons.Children.Add(use); footer.Children.Add(buttons);
        root.Children.Add(results); dialog.Content = root;
        void Compact()
        {
            introHelp.Visibility = numberHelp.Visibility = dialog.ActualHeight < 620 ? Visibility.Collapsed : Visibility.Visible;
            connection.Visibility = malAccount.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        }
        dialog.SizeChanged += (_, _) => Compact(); dialog.Loaded += (_, _) => Compact();
        MalAnimeCandidate? chosen = null;

        void UpdateSelection()
        {
            chosen = (results.SelectedItem as ListBoxItem)?.Tag as MalAnimeCandidate;
            bool account = trackingProfile is not null && malAccount.IsConnected;
            numberRow.Visibility = account && context.Detection is { Kind: AnimeKind.Episode, Episode: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            use.Content = UiText.Translate(account && numberRow.Visibility == Visibility.Visible ? "Track this show" : "Use this match");
            use.IsEnabled = chosen is not null;
            selection.Text = chosen is null ? "" : UiText.Translate("Selected: {0}", CandidateTitle(chosen) +
                (chosen.StartYear is { } year ? " · " + year : ""));
        }
        results.SelectionChanged += (_, _) => { UpdateSelection(); if (results.SelectedItem is { } selected) results.ScrollIntoView(selected); };
        query.TextChanged += (_, _) => { searching?.Cancel(); results.Items.Clear(); chosen = null; use.IsEnabled = false;
            status.Text = UiText.Translate("Search for alternatives if the detected title is wrong."); };
        use.Click += (_, _) =>
        {
            if (chosen is null || accountEpoch != accountGeneration) return;
            bool tracking = trackingProfile is not null && malAccount.IsConnected && context.Detection is { Kind: AnimeKind.Episode, Episode: > 0 };
            int episode = 0;
            if (tracking && (!int.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out episode) || episode is < 1 or > 10000 ||
                chosen.EpisodeCount > 0 && episode > chosen.EpisodeCount))
            { status.Text = UiText.Translate("Check the episode number against this season's episode count."); return; }
            RememberAnimeLink(context, chosen.Id, true, CandidateTitle(chosen));
            knownCandidates[chosen.Id] = chosen;
            Settings.DisabledTrackingKeys.RemoveAll(key => key.Equals(context.Key, StringComparison.OrdinalIgnoreCase));
            if (tracking)
            {
                var link = Settings.AnimeLinks[context.Key];
                Settings.TrackingApprovals[context.Key] = new(chosen.Id, trackingProfile!.Id, episode - context.Detection.Episode!.Value, link.Updated)
                    { ReviewedUncertainHints = true };
                trackingBlocked.Remove(chosen.Id); trackingNotes.Remove(chosen.Id);
            }
            SaveSettings(); dialog.Close();
        };
        ignore.Click += (_, _) => {
            CancelTrackingWrites(); Settings.TrackingApprovals.Remove(context.Key);
            if (!Settings.DisabledTrackingKeys.Contains(context.Key, StringComparer.OrdinalIgnoreCase)) Settings.DisabledTrackingKeys.Add(context.Key);
            SaveSettings(); dialog.Close();
        };
        connection.Click += (_, _) => { configure = true; dialog.Close(); };

        async Task AddCard(MalAnimeCandidate candidate, bool recommended)
        {
            var card = new Grid { Margin = new(5, 8, 5, 8) };
            card.ColumnDefinitions.Add(new() { Width = new(82) }); card.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var image = new Image { Width = 72, Height = 104, Stretch = Stretch.UniformToFill };
            var placeholder = new Grid(); placeholder.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.VideoClip24, FontSize = 28,
                Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }); placeholder.Children.Add(image);
            var poster = new Border { Background = (Brush)FindResource("SurfaceBrush"), CornerRadius = new(6), Width = 72, Height = 104, ClipToBounds = true, Child = placeholder };
            card.Children.Add(poster);
            var text = new StackPanel { Margin = new(8, 0, 8, 0) }; Grid.SetColumn(text, 1); card.Children.Add(text);
            var badges = new List<string>();
            if (recommended) badges.Add(UiText.Translate("Recommended"));
            if (accountEntries.ContainsKey(candidate.Id)) badges.Add(UiText.Translate("On your MAL list"));
            badges.Add(UiText.Translate(AnimeMatcher.MatchReason(context.Detection, candidate)));
            text.Children.Add(new TextBlock { Text = string.Join(" · ", badges), Foreground = (Brush)FindResource("AccentBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = CandidateTitle(candidate), FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) });
            if (!string.Equals(candidate.Title, CandidateTitle(candidate), StringComparison.Ordinal))
                text.Children.Add(new TextBlock { Text = candidate.Title, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 3, 0, 0) });
            text.Children.Add(new TextBlock { Text = (candidate.StartYear?.ToString() ?? UiText.Translate("Year unknown")) + " · " +
                candidate.MediaType.ToUpperInvariant() + " · " + (candidate.EpisodeCount > 0 ? UiText.Episodes(candidate.EpisodeCount) : UiText.Translate("Episode count unknown")),
                Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new(0, 6, 0, 0) });
            if (candidate.Synopsis.Length > 0) text.Children.Add(new TextBlock { Text = candidate.Synopsis, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new(0, 6, 0, 0) });
            var entry = new ListBoxItem { Content = card, Tag = candidate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            results.Items.Add(entry);
            if (recommended && results.SelectedItem is null) results.SelectedItem = entry;
            try { var cover = await matchCovers.LoadAsync(candidate.PictureUrl, lifetime.Token); if (alive && !lifetime.IsCancellationRequested) image.Source = cover; }
            catch (OperationCanceledException) { }
        }
        void Fill(IEnumerable<MalAnimeCandidate> candidates)
        {
            results.Items.Clear();
            var decision = AnimeMatcher.Rank(context.Detection with { Title = query.Text.Trim() }, candidates);
            foreach (var candidate in decision.RankedCandidates.Take(10)) _ = AddCard(candidate, decision.RecommendedMatch?.Id == candidate.Id);
            if (Settings.AnimeLinks.TryGetValue(context.Key, out var saved))
            {
                var selected = results.Items.Cast<ListBoxItem>().FirstOrDefault(x => ((MalAnimeCandidate)x.Tag).Id == saved.MalId);
                if (selected is not null) results.SelectedItem = selected;
                if (Settings.TrackingApprovals.TryGetValue(context.Key, out var approval) && context.Detection.Episode is { } local && approval.MalId == saved.MalId)
                    number.Text = (local + approval.EpisodeOffset).ToString(CultureInfo.InvariantCulture);
            }
            UpdateSelection();
        }
        async Task Search()
        {
            searching?.Cancel();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); searching = request;
            string title = query.Text.Trim(); search.IsEnabled = false;
            status.Text = UiText.Translate("Looking for a MyAnimeList match…");
            try
            {
                if (MalClientId is null) { status.Text = UiText.Translate("MyAnimeList isn't configured in this build. Try the latest Koma release."); return; }
                var candidates = await SearchAnime(title, request.Token, explicitSearch: true);
                if (!alive || request.IsCancellationRequested || searching != request || accountEpoch != accountGeneration) return;
                foreach (var candidate in candidates) knownCandidates[candidate.Id] = candidate;
                Fill(candidates);
                status.Text = UiText.Translate(candidates.Count == 0 ? "No match found. Try the English or original title." : "Check the year and season, then choose Track this show.");
            }
            catch (OperationCanceledException) { if (alive && !request.IsCancellationRequested) status.Text = UiText.Translate("The search timed out. Your saved match is unchanged."); }
            catch (Exception error) when (error is HttpRequestException or ArgumentException or InvalidOperationException)
            { if (alive && searching == request) status.Text = UiText.Translate("The search failed. Your saved match is unchanged."); }
            finally { if (searching == request) { searching = null; search.IsEnabled = true; } }
        }
        search.Click += async (_, _) => await Search();
        query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Search(); } };
        var personal = MatchMyList(context);
        if (personal.RecommendedMatch is not null)
            Fill(personal.RankedCandidates.Take(6));
        else if (recognitionCache.TryGetValue(AnimeRecognition.NormalizeTitle(context.Detection.Title), out var recent) &&
            DateTimeOffset.UtcNow - recent.At < TimeSpan.FromMinutes(5)) Fill(recent.Items);
        else if (Settings.AnimeLinks.TryGetValue(context.Key, out var saved) &&
            (knownCandidates.GetValueOrDefault(saved.MalId) ?? accountEntries.GetValueOrDefault(saved.MalId)?.Candidate) is { } remembered)
            Fill([remembered]);
        if (MalClientId is not null && results.Items.Count == 0)
            dialog.Loaded += async (_, _) => await Search();
        dialog.Closed += (_, _) => { alive = false; lifetime.Cancel(); searching?.Cancel(); };
        dialog.ShowDialog();
        UpdateRecognition(); UpdatePlaybackTracking(); RenderTracking();
        if (configure)
        {
            if (MalClientId is not null) ConnectTracking(this, new());
            else ConfigureTrackingClient();
        }
        else if (Settings.AutoTrackAnime) _ = SyncCompletedEpisodes();
    }

    private TextBlock ChooserNote(string text) => new() { Text = UiText.Translate(text), TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new(0, 6, 0, 0) };
    private static string CandidateTitle(MalAnimeCandidate candidate) => candidate.EnglishTitle.Length > 0 ? candidate.EnglishTitle : candidate.Title;
}
