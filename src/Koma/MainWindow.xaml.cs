using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Koma;

public partial class MainWindow : Window
{
    private const string VideoDialogFilter = Library.Filter;
    private static readonly Guid DefaultFolderId = Library.DefaultFolderId;
    private readonly PreferenceStore store;
    private readonly Controls transport = new();
    private readonly ObservableCollection<Episode> queue = [];
    private ScannedFolder[] scanned = [];
    private Season? displayedSeason;
    private Episode? continueEpisode;
    private CancellationTokenSource? scanning;
    private CancellationTokenSource? opening;
    private DateTimeOffset lastScan;
    private bool closing;
    private bool displayingFolders;

    private Preferences Settings => store.Data;
    private record EpisodeRow(Episode Episode, string Label);

    public MainWindow(string[] paths)
    {
        InitializeComponent();
        string app = AppContext.BaseDirectory;
        bool portable = File.Exists(Path.Combine(app, "portable.marker"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string data = portable ? Path.Combine(app, "data") : Path.Combine(local, "Koma");
        string legacy = Path.Combine(app, "portable_config", "player-settings.json");
        store = new(data, legacy, portable ? null : Path.Combine(local, "Local Anime Player"));
        statusDuringStartup = store.Notice;
        if (!Settings.Folders.Any(x => x.Id == DefaultFolderId))
            Settings.Folders.Insert(0, new(DefaultFolderId, Library.DefaultPath(), true));
        try
        {
            Folder root = Settings.Folders.Single(x => x.Id == DefaultFolderId);
            if (string.Equals(root.Path, Library.DefaultPath(), StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(root.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            statusDuringStartup = "The Anime folder could not be created. Choose another folder with Add folder.";
        }
        QueueList.ItemsSource = queue;
        ControlsDock.Children.Add(transport);
        ConfigurePlayback();
        Loaded += async (_, _) =>
        {
            SetStatus(statusDuringStartup);
            _ = RefreshLibrary();
            if (paths.Length > 0) await OpenPaths(paths, false);
        };
        Activated += (_, _) =>
        {
            if (IsLoaded && DateTimeOffset.UtcNow - lastScan > TimeSpan.FromSeconds(1))
                _ = RefreshLibrary();
        };
        Closing += (_, _) =>
        {
            closing = true;
            scanning?.Cancel();
            opening?.Cancel();
            Remember();
            SaveSettings();
            ClosePlayback();
        };
    }

    private readonly string? statusDuringStartup;

    private void SetStatus(string? message)
    {
        Status.Text = message ?? "";
        Status.Visibility = string.IsNullOrEmpty(message) || fullscreen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveSettings()
    {
        try { store.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SetStatus("Settings could not be saved. Check that the app data folder is writable."); }
    }

    private async Task RefreshLibrary(Guid? select = null)
    {
        if (closing) return;
        if (scanning is not null && select is null) return;
        scanning?.Cancel();
        using var request = new CancellationTokenSource();
        scanning = request;
        lastScan = DateTimeOffset.UtcNow;
        var roots = Settings.Folders.ToArray();
        if (scanned.Length == 0) LibraryMessage.Text = "Looking for episodes…";
        try
        {
            ScannedFolder[] items = await Task.Run(() => Library.Scan(roots, request.Token), request.Token);
            if (closing || scanning != request) return;
            var selected = select ?? (Folders.SelectedItem as ScannedFolder)?.Folder.Id;
            string? seasonName = displayedSeason?.Name;
            string? seasonKey = displayedSeason?.Episodes.FirstOrDefault()?.Key;
            displayingFolders = true;
            scanned = items;
            Folders.ItemsSource = scanned;
            Folders.SelectedItem = scanned.FirstOrDefault(x => x.Folder.Id == selected) ?? scanned.FirstOrDefault();
            displayingFolders = false;
            RenderFolder(seasonName, seasonKey);
            RenderContinue();
        }
        catch (OperationCanceledException) { }
        finally { if (scanning == request) scanning = null; }
    }

    private void FolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!displayingFolders) RenderFolder(null, null);
    }

    private void RenderFolder(string? seasonName, string? episodeKey)
    {
        displayedSeason = null;
        Shows.Items.Clear();
        var selected = Folders.SelectedItem as ScannedFolder;
        Locate.Visibility = selected is { Available: false } ? Visibility.Visible : Visibility.Collapsed;
        RemoveRoot.Visibility = selected is { Folder.IsDefault: false } ? Visibility.Visible : Visibility.Collapsed;
        LibraryMessage.Text = selected is null ? "" : !selected.Available ? "This folder is unavailable."
            : selected.Collections.Length == 0 ? "Add videos to this folder, then refresh." : "";
        TreeViewItem? first = null, previous = null;
        foreach (Collection group in selected?.Collections ?? [])
        {
            var node = TreeItem(group.Name);
            Shows.Items.Add(node);
            if (group.Seasons.Length == 1 && group.Seasons[0].Name == group.Name)
                Register(node, group.Seasons[0]);
            else
                foreach (Season season in group.Seasons)
                {
                    var child = TreeItem(season.Name);
                    node.Items.Add(child);
                    Register(child, season);
                }
        }
        if ((previous ?? first) is { } match) match.IsSelected = true;
        else RenderEpisodes();

        void Register(TreeViewItem item, Season season)
        {
            item.Tag = season;
            first ??= item;
            if (season.Name == seasonName && season.Episodes.Any(x => x.Key == episodeKey)) previous = item;
        }
    }

    private static TreeViewItem TreeItem(string title) => new() { Header = title, IsExpanded = true, Padding = new Thickness(5) };

    private void SeasonSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        displayedSeason = (e.NewValue as TreeViewItem)?.Tag as Season;
        RenderEpisodes();
    }

    private void RenderEpisodes()
    {
        EpisodeHeading.Text = displayedSeason?.Name ?? "Episodes";
        PlaySeason.IsEnabled = displayedSeason is not null;
        Episodes.ItemsSource = displayedSeason?.Episodes.Select(episode =>
        {
            Progress? progress = FindProgress(episode);
            string label = progress is { Watched: true } ? "✓  " + episode.Name
                : progress is { Seconds: > 0 } ? $"{episode.Name}  ·  {FormatTime(progress.Seconds)}" : episode.Name;
            return new EpisodeRow(episode, label);
        }).ToArray();
    }

    private Progress? FindProgress(Episode episode) =>
        Settings.History.GetValueOrDefault(episode.Key) ?? Settings.History.GetValueOrDefault(episode.Path);

    private void RenderContinue()
    {
        continueEpisode = null;
        DateTimeOffset suggestedAt = DateTimeOffset.MinValue;
        var seasons = scanned.SelectMany(x => x.Collections).SelectMany(x => x.Seasons).ToArray();
        var candidates = seasons.SelectMany(season => season.Episodes.Select((episode, index) =>
            (Season: season, Episode: episode, Index: index, Progress: FindProgress(episode))))
            .Where(x => x.Progress is not null).OrderByDescending(x => x.Progress!.Updated);
        foreach (var item in candidates)
        {
            if (item.Progress is { Watched: false, Seconds: > 0 } && Settings.Resume)
            {
                continueEpisode = item.Episode;
                ContinueButton.Content = "Resume";
                suggestedAt = item.Progress.Updated;
                break;
            }
            if (item.Progress is not { Watched: true }) continue;
            var next = item.Season.Episodes.Skip(item.Index + 1).FirstOrDefault(x => FindProgress(x) is not { Watched: true });
            if (next is null) continue;
            continueEpisode = next;
            ContinueButton.Content = "Play next";
            suggestedAt = item.Progress.Updated;
            break;
        }
        if (Settings.Resume)
        {
            var recent = Settings.History.Where(x => !x.Value.Watched && x.Value.Seconds > 0 &&
                x.Value.Updated > suggestedAt && Path.IsPathFullyQualified(x.Key) && File.Exists(x.Key) &&
                !seasons.Any(s => s.Episodes.Any(ep => string.Equals(ep.Path, x.Key, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(x => x.Value.Updated).FirstOrDefault();
            if (recent.Key is not null)
            {
                continueEpisode = Library.Identify(recent.Key, Settings.Folders);
                ContinueButton.Content = "Resume";
            }
        }
        ContinueTitle.Text = continueEpisode?.Name ?? "";
        ContinueCard.Visibility = continueEpisode is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowHome(object sender, RoutedEventArgs e)
    {
        PlayerPane.Visibility = Visibility.Collapsed;
        HomePane.Visibility = Visibility.Visible;
        Remember();
        RenderEpisodes();
        RenderContinue();
    }

    private void ShowPlayer(object sender, RoutedEventArgs e) => DisplayPlayer();

    private void DisplayPlayer()
    {
        HomePane.Visibility = Visibility.Collapsed;
        PlayerPane.Visibility = Visibility.Visible;
        PlayerTab.IsEnabled = true;
    }

    private async void RefreshFolders(object sender, RoutedEventArgs e) => await RefreshLibrary();

    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        string? path = PickFolder();
        if (path is null) return;
        var existing = Settings.Folders.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        var root = existing ?? new Folder(Guid.NewGuid(), path);
        if (existing is null) Settings.Folders.Add(root);
        SaveSettings();
        await RefreshLibrary(root.Id);
    }

    private async void LocateFolder(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is not ScannedFolder item || PickFolder() is not { } path) return;
        if (Settings.Folders.Any(x => x.Id != item.Folder.Id && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("That location is already saved.");
            return;
        }
        Settings.Folders[Settings.Folders.IndexOf(item.Folder)] = item.Folder with { Path = path };
        SaveSettings();
        await RefreshLibrary(item.Folder.Id);
    }

    private async void RemoveFolder(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is not ScannedFolder { Folder.IsDefault: false } item) return;
        Settings.Folders.Remove(item.Folder);
        SaveSettings();
        await RefreshLibrary(DefaultFolderId);
    }

    private string? PickFolder()
    {
        var picker = new OpenFolderDialog { Title = "Select an anime folder" };
        return picker.ShowDialog(this) == true ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(picker.FolderName)) : null;
    }

    private void OpenAnimeFolder(object sender, RoutedEventArgs e)
    {
        string path = Settings.Folders.Single(x => x.Id == DefaultFolderId).Path;
        try
        {
            if (!Directory.Exists(path) && string.Equals(path, Library.DefaultPath(), StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(path);
            if (!Directory.Exists(path)) { SetStatus("Locate the Anime folder before opening it."); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { SetStatus("The Anime folder could not be opened."); }
    }

    private async void PlaySelectedSeason(object sender, RoutedEventArgs e)
    {
        if (displayedSeason is not { Episodes.Length: > 0 } season) return;
        opening?.Cancel();
        await UseQueue(season.Episodes, season.Episodes[0]);
    }

    private async void EpisodeDoubleClick(object sender, MouseButtonEventArgs e) => await PlayEpisode();
    private async void EpisodeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await PlayEpisode(); }
    }
    private async Task PlayEpisode()
    {
        if (Episodes.SelectedItem is not EpisodeRow row || displayedSeason is not { } season) return;
        opening?.Cancel();
        await UseQueue(season.Episodes, row.Episode);
    }

    private async void ContinueWatching(object sender, RoutedEventArgs e)
    {
        if (continueEpisode is not { } episode) return;
        opening?.Cancel();
        var season = scanned.SelectMany(x => x.Collections).SelectMany(x => x.Seasons)
            .FirstOrDefault(x => x.Episodes.Contains(episode));
        await UseQueue(season?.Episodes ?? [episode], episode);
    }

    private async void OpenFiles(object sender, RoutedEventArgs e) => await PickFiles(false);
    private async void AddToQueue(object sender, RoutedEventArgs e) => await PickFiles(true);
    private async Task PickFiles(bool append)
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = VideoDialogFilter };
        if (picker.ShowDialog(this) == true) await OpenPaths(picker.FileNames, append);
    }

    private void DragIn(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropIn(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await OpenPaths(paths, false);
        }
    }

    private Task OpenNativeDrop(JsonElement data)
    {
        if (data.Value("files") is not { ValueKind: JsonValueKind.Array } files) return Task.CompletedTask;
        string[] paths = files.EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.String && Path.IsPathFullyQualified(file.GetString()!))
            .Select(file => file.GetString()!).ToArray();
        if (paths.Length == 0) return Task.CompletedTask;
        return OpenPaths(paths, data.String("action") == "append");
    }

    private async Task OpenPaths(string[] paths, bool append)
    {
        opening?.Cancel();
        using var request = new CancellationTokenSource();
        opening = request;
        var folders = Settings.Folders.ToArray();
        try
        {
            Episode[] found = await Task.Run(() => Library.Expand(paths, folders, request.Token), request.Token);
            if (closing || opening != request) return;
            if (found.Length == 0) { SetStatus("No videos found in the selected files or folders."); return; }
            if (append && queue.Count != 0)
            {
                foreach (Episode episode in found)
                    if (!queue.Any(x => string.Equals(x.Path, episode.Path, StringComparison.OrdinalIgnoreCase))) queue.Add(episode);
                UpdateTransport();
            }
            else await UseQueue(found, found[0]);
        }
        catch (OperationCanceledException) { }
        finally { if (opening == request) opening = null; }
    }

    private async Task UseQueue(Episode[] episodes, Episode first)
    {
        if (!CanOpen(first)) return;
        queue.Clear();
        foreach (var episode in episodes) queue.Add(episode);
        await Play(first);
    }

    private async void QueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not Episode episode) return;
        opening?.Cancel();
        await Play(episode);
    }
    private void QueueUp(object sender, RoutedEventArgs e) => MoveQueue(-1);
    private void QueueDown(object sender, RoutedEventArgs e) => MoveQueue(1);
    private void MoveQueue(int offset)
    {
        int selected = QueueList.SelectedIndex;
        if (selected >= 0 && selected + offset >= 0 && selected + offset < queue.Count)
        {
            queue.Move(selected, selected + offset);
            QueueList.SelectedIndex = selected + offset;
            UpdateTransport();
        }
    }
    private void RemoveEpisode(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not Episode episode) return;
        if (episode == playing)
        {
            Remember();
            ClosePlayback();
        }
        queue.Remove(episode);
        UpdateTransport();
    }
}
