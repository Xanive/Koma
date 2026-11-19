using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Koma;

internal sealed class ShowCard(Collection collection) : INotifyPropertyChanged
{
    public Collection Collection { get; } = collection;
    public string Name => Collection.DisplayName;
    public string Summary => UiText.Episodes(Collection.Seasons.Sum(x => x.Episodes.Length)) + " · " + UiText.Seasons(Collection.Seasons.Length);
    private BitmapSource? artwork;
    public BitmapSource? Artwork { get => artwork; set { artwork = value; PropertyChanged?.Invoke(this, new(nameof(Artwork))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private readonly ArtworkCache artwork;
    private ShowCard[] cards = [];
    private Collection? selectedCollection;
    private readonly Dictionary<Button, CancellationTokenSource> artworkRequests = [];
    private int cardColumns;
    private bool queueOpen;
    private double libraryOffset;
    private bool changingSeason;
    private record SeasonChoice(Season Value, string Name);
    private bool startingPlayback;

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(240d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    private void ShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized || Sidebar is null) return;
        UpdateShell();
        ReflowCards();
    }

    private void UpdateShell()
    {
        bool compact = PlayerPane.IsVisible || ActualWidth < 900;
        SidebarWidth.Width = new(fullscreen ? 0 : compact ? 72 : 200);
        Sidebar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in new[] { BrandLabel, LibraryLabel, PlayerLabel, TrackingLabel, DownloadsLabel, FolderLabel, AddLabel, OpenFolderLabel, SettingsLabel })
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandLabel.Visibility = FolderLabel.Visibility = compact || ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        LibraryTab.Background = HomePane.IsVisible ? (Brush)FindResource("HeroBrush") : Brushes.Transparent;
        PlayerTab.Background = PlayerPane.IsVisible ? (Brush)FindResource("HeroBrush") : Brushes.Transparent;
        TrackingTab.Background = TrackingPane.IsVisible ? (Brush)FindResource("HeroBrush") : Brushes.Transparent;
        DownloadsTab.Background = DownloadsPane.IsVisible ? (Brush)FindResource("HeroBrush") : Brushes.Transparent;
        QueuePane.Visibility = queueOpen && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        TrackingStats.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        TrackingSubtitle.Visibility = TrackingHelp.Visibility = TrackingConnection.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        TrackingAccountCard.Padding = new Thickness(ActualHeight < 650 ? 12 : 18);
        DownloadsSubtitle.Visibility = DownloadSharingNote.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        if (downloadsReady) RenderDownloads();
        UpdatePlaybackTracking();
        if (artwork is not null) UpdateArtworkActivity();
    }

    private void ToggleQueue()
    {
        if (fullscreen) return;
        queueOpen = !queueOpen;
        UpdateShell();
    }

    private void OpenMore()
    {
        var entries = new List<(string, bool, Func<Task>)> {
            (UiText.Translate(reference is null ? "Compare with original" : "Close comparison"), reference is not null, ToggleCompare)
        };
        if (!transport.Speed.IsVisible)
            entries.Add((UiText.Translate("Playback speed"), false, () => { transport.Speed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; }));
        if (!transport.Chapters.IsVisible && transport.Chapters.IsEnabled)
            entries.Add((UiText.Translate("Chapters"), false, () => { transport.Chapters.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; }));
        OpenMenu(transport.More, entries);
    }

    private void ReflowCards()
    {
        if (ShowCards is null) return;
        double width = ShowCards.ActualWidth > 40 ? Math.Max(180, ShowCards.ActualWidth - 18) :
            Math.Max(230, ActualWidth - SidebarWidth.Width.Value - 72);
        int columns = Math.Clamp((int)(width / 230), 1, 5);
        CardWidth = Math.Max(180, (width - 16 * columns) / columns);
        var rows = cards.Chunk(columns).ToArray();
        double offset = ScrollOffset(ShowCards);
        bool changed = cardColumns != columns || ShowCards.ItemsSource is not ShowCard[][] previous ||
            !previous.SelectMany(x => x).SequenceEqual(cards);
        if (!changed) return;
        cardColumns = columns;
        ShowCards.ItemsSource = rows;
        RestoreScroll(ShowCards, offset);
    }

    private void CardsSizeChanged(object sender, SizeChangedEventArgs e) => ReflowCards();

    private void OpenShow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShowCard card }) return;
        libraryOffset = ScrollOffset(ShowCards);
        DisplayCollection(card.Collection, null);
    }

    private void DisplayCollection(Collection collection, string? season)
    {
        selectedCollection = collection;
        PageTitle.Text = collection.DisplayName;
        LibrarySummary.Text = UiText.Episodes(collection.Seasons.Sum(x => x.Episodes.Length));
        ShowCards.Visibility = Visibility.Collapsed;
        ShowDetails.Visibility = Visibility.Visible;
        var saved = displayedSeason;
        changingSeason = true;
        var choices = collection.Seasons.Select(value => new SeasonChoice(value,
            collection.IsLoose ? UiText.Translate("Loose videos") : value.Name)).ToArray();
        Seasons.ItemsSource = choices;
        Seasons.SelectedItem = choices.FirstOrDefault(x => x.Value.Name == season) ?? choices.FirstOrDefault();
        changingSeason = false;
        displayedSeason = (Seasons.SelectedItem as SeasonChoice)?.Value;
        RenderEpisodes();
        if (saved?.Name != displayedSeason?.Name) RestoreScroll(Episodes, 0);
    }

    private void BackToLibrary(object sender, RoutedEventArgs e)
    {
        recognitionRequest?.Cancel();
        recognitionInFlight = null;
        recognitionContext = null;
        RecognitionPanel.Visibility = Visibility.Collapsed;
        selectedCollection = null;
        displayedSeason = null;
        ShowDetails.Visibility = Visibility.Collapsed;
        ShowCards.Visibility = Visibility.Visible;
        PageTitle.Text = UiText.Translate("Your library");
        LibrarySummary.Text = Folders.SelectedItem is ScannedFolder root ? $"{root.Folder.Name} · {UiText.Collections(cards.Length)}" : UiText.Translate("Choose a folder to begin");
        RestoreScroll(ShowCards, libraryOffset);
    }

    private void SeasonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSeason) return;
        displayedSeason = (Seasons.SelectedItem as SeasonChoice)?.Value;
        RenderEpisodes();
        RestoreScroll(Episodes, 0);
    }

    private async void PlayEpisodeRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EpisodeRow row }) return;
        e.Handled = true;
        Episodes.SelectedItem = row;
        await PlayEpisode();
    }

    private async void CardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShowCard card } button || artwork is null) return;
        if (artworkRequests.Remove(button, out var previous)) { previous.Cancel(); previous.Dispose(); }
        using var request = new CancellationTokenSource();
        artworkRequests[button] = request;
        var image = await artwork.Load(card.Collection, request.Token);
        if (!request.IsCancellationRequested && button.Tag == card && image is not null) card.Artwork = image;
        if (artworkRequests.GetValueOrDefault(button) == request) artworkRequests.Remove(button);
    }

    private void CardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && artworkRequests.Remove(button, out var request)) request.Cancel();
    }

    private void UpdateArtworkActivity()
    {
        bool active = startingPlayback || loaded && !paused;
        if (!artwork.SetPlayback(active) || active || !HomePane.IsVisible) return;
        foreach (var button in Descendants<Button>(ShowCards).Where(x => x.Tag is ShowCard)) CardLoaded(button, new());
    }

    private void ArtworkResized(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image image) image.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static double ScrollOffset(DependencyObject control) => Descendants<ScrollViewer>(control).FirstOrDefault()?.VerticalOffset ?? 0;
    private void RestoreScroll(DependencyObject control, double offset) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
        () => Descendants<ScrollViewer>(control).FirstOrDefault()?.ScrollToVerticalOffset(offset));
}
