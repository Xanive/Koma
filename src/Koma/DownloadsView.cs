using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Koma;

public partial class MainWindow
{
    private readonly TorrentEngine torrentEngine;
    private readonly ObservableCollection<DownloadRow> downloadRows = [];
    private readonly DispatcherTimer downloadTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource downloadLifetime = new();
    private CancellationTokenSource? feedRequest;
    private HttpClient? downloadHttp;
    private IReadOnlyList<ReleaseFeedEntry> releaseEntries = [];
    private bool downloadsReady, downloadBrowse, downloadShuttingDown, downloadShutdownFinished;
    private string downloadMessage = "Downloads stay paused after reopening Koma. Choose Resume to continue.";
    private string feedMessage = "Search Nyaa for an anime title, or leave the search blank to browse recent releases.";
    private string DownloadRoot => string.IsNullOrWhiteSpace(Settings.DownloadFolder) ?
        Settings.Folders.Single(x => x.Id == DefaultFolderId).Path : Settings.DownloadFolder;
    private sealed record DownloadChoice(string Key, string Name);
    private sealed record ReleaseRow(ReleaseFeedEntry Entry)
    {
        public string Title => Entry.Title;
        public string Summary => string.Join(" · ", new[] { DownloadSources.Quality(Entry.Title),
            Entry.SizeBytes.HasValue ? DownloadSize(Entry.SizeBytes.Value) : "",
            Entry.Published?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "" }.Where(x => x.Length > 0));
    }

    private sealed class DownloadRow(TorrentJobInfo info) : INotifyPropertyChanged
    {
        public TorrentJobInfo Info { get; private set; } = info;
        public string Name => Info.Name;
        public double Progress => Info.Progress;
        public string PercentText => Info.Progress.ToString("0.0", CultureInfo.CurrentCulture) + "%";
        public string Summary => UiText.Translate(Info.State) + " · " + DownloadSize(Info.DownloadedBytes) + " / " +
            (Info.TotalBytes > 0 ? DownloadSize(Info.TotalBytes) : "?") + " · ↓ " + DownloadSize(Info.DownloadBytesPerSecond) + "/s · ↑ " + DownloadSize(Info.UploadBytesPerSecond) + "/s";
        public string Error => UiText.Translate(Info.Error ?? "");
        public bool CanStart => Info.State is "Queued" or "Paused" or "Error";
        public bool CanPause => Info.State is "Starting" or "Metadata" or "Checking" or "Downloading";
        public bool CanOpen => Info.State == "Completed" && Directory.Exists(Info.Destination);
        public void Update(TorrentJobInfo value) { Info = value; PropertyChanged?.Invoke(this, new(null)); }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void InitializeDownloads()
    {
        downloadsReady = true; DownloadJobs.ItemsSource = downloadRows;
        torrentEngine.Changed += () => Dispatcher.BeginInvoke(() => { if (!closing) RenderDownloads(); });
        torrentEngine.Completed += _ => Dispatcher.BeginInvoke(async () =>
        {
            if (closing) return;
            RememberDownloadPublications();
            await RefreshAfterDownload(); RenderDownloads();
        });
        downloadTimer.Tick += (_, _) => { if (DownloadsPane.IsVisible) RenderDownloads(); };
        downloadMessage = torrentEngine.Notice ?? downloadMessage;
        RememberDownloadPublications();
        downloadTimer.Start(); RefreshDownloadsLanguage();
        _ = ApplyDownloadLimits();
        Loaded += async (_, _) =>
        {
            try { await torrentEngine.RecoverCompletedAsync(downloadLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (DownloadError(error))
            { downloadMessage = "Completed downloads could not be checked. Your files were kept."; RenderDownloads(); }
        };
    }

    private async Task ApplyDownloadLimits()
    {
        try { await torrentEngine.LimitsAsync(Settings.DownloadLimitKiB, Settings.UploadLimitKiB, downloadLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (DownloadError(error)) { downloadMessage = "Download settings could not be applied."; }
    }

    private void RefreshDownloadsLanguage()
    {
        if (!downloadsReady) return;
        string category = (DownloadCategory.SelectedItem as DownloadChoice)?.Key ?? "1_2";
        string quality = (DownloadQuality.SelectedItem as DownloadChoice)?.Key ?? "";
        DownloadCategory.ItemsSource = new[] { new DownloadChoice("1_2", UiText.Translate("English-translated anime")),
            new DownloadChoice("1_3", UiText.Translate("Other translated anime")), new DownloadChoice("1_4", UiText.Translate("Raw anime")),
            new DownloadChoice("1_0", UiText.Translate("All anime")) };
        DownloadCategory.SelectedItem = DownloadCategory.Items.Cast<DownloadChoice>().First(x => x.Key == category);
        DownloadQuality.ItemsSource = new[] { new DownloadChoice("", UiText.Translate("Any quality")), new DownloadChoice("2160p", "2160p"),
            new DownloadChoice("1080p", "1080p"), new DownloadChoice("720p", "720p"), new DownloadChoice("480p", "480p") };
        DownloadQuality.SelectedItem = DownloadQuality.Items.Cast<DownloadChoice>().First(x => x.Key == quality);
        FilterDownloadReleases(); RenderDownloads();
    }

    private void ShowDownloads(object sender, RoutedEventArgs e)
    {
        Remember(); HomePane.Visibility = PlayerPane.Visibility = TrackingPane.Visibility = Visibility.Collapsed;
        DownloadsPane.Visibility = Visibility.Visible; RenderDownloads(); UpdateShell();
    }
    private void ShowDownloadQueue(object sender, RoutedEventArgs e) { downloadBrowse = false; RenderDownloads(); }
    private void ShowDownloadBrowse(object sender, RoutedEventArgs e) { downloadBrowse = true; RenderDownloads(); }

    private void RenderDownloads()
    {
        if (!downloadsReady || closing) return;
        var snapshot = torrentEngine.GetSnapshot();
        foreach (var item in downloadRows.Where(row => !snapshot.Any(x => x.Id == row.Info.Id)).ToArray()) downloadRows.Remove(item);
        foreach (var item in snapshot)
        {
            if (downloadRows.FirstOrDefault(x => x.Info.Id == item.Id) is { } row) row.Update(item);
            else downloadRows.Add(new(item));
        }
        DownloadEmpty.Visibility = downloadRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadJobs.Visibility = downloadRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DownloadMessage.Text = UiText.Translate(downloadBrowse ? feedMessage : downloadMessage);
        DownloadMessage.ToolTip = DownloadMessage.Text;
        DownloadDestination.Text = UiText.Translate("Save completed downloads in {0}", DownloadRoot);
        DownloadDestination.ToolTip = DownloadRoot;
        DownloadTotals.Text = UiText.Translate("{0} downloads · ↓ {1}/s · ↑ {2}/s", snapshot.Count,
            DownloadSize(snapshot.Sum(x => x.DownloadBytesPerSecond)), DownloadSize(snapshot.Sum(x => x.UploadBytesPerSecond)));
        DownloadQueuePane.Visibility = downloadBrowse ? Visibility.Collapsed : Visibility.Visible;
        DownloadBrowsePane.Visibility = downloadBrowse ? Visibility.Visible : Visibility.Collapsed;
        DownloadQueueTab.Background = !downloadBrowse ? (Brush)FindResource("HeroBrush") : (Brush)FindResource("SurfaceBrush");
        DownloadBrowseTab.Background = downloadBrowse ? (Brush)FindResource("HeroBrush") : (Brush)FindResource("SurfaceBrush");
        DownloadSourceNote.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        DownloadAddMagnetButton.Visibility = DownloadOpenTorrentButton.Visibility = downloadBrowse && ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        DownloadMessage.MaxHeight = ActualHeight < 650 ? 38 : double.PositiveInfinity;
    }

    private async void AddDownloadMagnet(object sender, RoutedEventArgs e)
    {
        var dialog = TrackingDialog("Add magnet", out var body);
        body.Children.Add(TrackingNote("Paste a magnet link. Downloads run inside Koma."));
        var field = new TextBox { MaxLength = 16384, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 14, 0, 12), MinHeight = 70, Padding = new(8) };
        System.Windows.Automation.AutomationProperties.SetName(field, UiText.Translate("Magnet link")); body.Children.Add(field);
        body.Children.Add(TrackingNote(UiText.Translate("Save completed downloads in {0}", DownloadRoot)));
        body.Children.Add(TrackingNote("Torrents upload while downloading. Koma stops sharing when a download completes."));
        var note = TrackingNote(""); body.Children.Add(note);
        var add = new Button { Content = UiText.Translate("Download"), HorizontalAlignment = HorizontalAlignment.Right };
        string? magnet = null;
        add.Click += (_, _) =>
        {
            if (!ReleaseSource.TryParseMagnet(field.Text.Trim(), out var uri, out _)) { note.Text = UiText.Translate("Enter a valid BitTorrent magnet link."); return; }
            magnet = uri!.AbsoluteUri; field.Clear(); dialog.Close();
        };
        body.Children.Add(add); dialog.Closed += (_, _) => field.Clear(); dialog.ShowDialog();
        if (magnet is not null) await QueueDownload(async token => await torrentEngine.AddMagnetAsync(magnet, token));
    }

    private async void OpenDownloadTorrent(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = UiText.Translate("Open torrent"), Filter = "BitTorrent|*.torrent", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await QueueDownload(async token => await torrentEngine.AddTorrentAsync(picker.FileName, token));
    }

    private async Task QueueDownload(Func<CancellationToken, Task<TorrentJobInfo>> add)
    {
        if (closing) return;
        try
        {
            torrentEngine.SetDownloadRoot(DownloadRoot);
            EnsureDownloadLibraryRoot();
            var job = await add(downloadLifetime.Token);
            ShowDownloads(this, new()); downloadBrowse = false; downloadMessage = "Starting download…"; RenderDownloads();
            await torrentEngine.StartAsync(job.Id, downloadLifetime.Token);
            string? state = torrentEngine.GetSnapshot().FirstOrDefault(x => x.Id == job.Id)?.State;
            downloadMessage = state == "Error" ? "The download could not start. Check the source and destination, then try again." :
                state == "Paused" ? "Paused" : "Downloads continue while Koma is open. Pause or remove them whenever you need.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (DownloadError(error)) { downloadMessage = "The download could not start. Check the source and destination, then try again."; }
        finally { RenderDownloads(); }
    }

    private void EnsureDownloadLibraryRoot()
    {
        string path = Path.GetFullPath(DownloadRoot);
        bool saved = Settings.Folders.Any(root =>
        {
            string relative = Path.GetRelativePath(root.Path, path);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        });
        if (!saved) { Settings.Folders.Add(new(Guid.NewGuid(), path)); SaveSettings(); }
    }

    private async void ResumeDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRow row }) return;
        await DownloadAction(() => torrentEngine.StartAsync(row.Info.Id, downloadLifetime.Token));
    }
    private async void PauseDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRow row }) return;
        await DownloadAction(() => torrentEngine.PauseAsync(row.Info.Id, downloadLifetime.Token));
    }
    private async void RemoveDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRow row }) return;
        if (await DownloadAction(() => torrentEngine.RemoveAsync(row.Info.Id, downloadLifetime.Token)))
            downloadMessage = "Removed from the queue. Downloaded files were kept.";
        RenderDownloads();
    }
    private async Task<bool> DownloadAction(Func<Task> action)
    {
        try { await action(); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (DownloadError(error)) { downloadMessage = "The download action failed. Your files were kept."; return false; }
        finally { RenderDownloads(); }
    }
    private void OpenDownloadFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRow { CanOpen: true } row }) return;
        try { Process.Start(new ProcessStartInfo(row.Info.Destination) { UseShellExecute = true, Verb = "open" }); }
        catch (Exception error) when (DownloadError(error)) { downloadMessage = "The download folder could not be opened."; RenderDownloads(); }
    }

    private async void ConfigureDownloads(object sender, RoutedEventArgs e)
    {
        var dialog = TrackingDialog("Download settings", out var body);
        body.Children.Add(TrackingNote("Download folder"));
        var path = new TextBox { Text = DownloadRoot, IsReadOnly = true, Padding = new(8), TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetName(path, UiText.Translate("Download folder")); body.Children.Add(path);
        var choose = new Button { Content = UiText.Translate("Choose folder"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 8) };
        choose.Click += (_, _) => { var picker = new OpenFolderDialog { Title = UiText.Translate("Download folder") }; if (picker.ShowDialog(dialog) == true) path.Text = picker.FolderName; };
        body.Children.Add(choose); body.Children.Add(TrackingNote("New downloads use this folder. Existing downloads keep their original destination."));
        body.Children.Add(TrackingNote("Download limit (KiB/s; 0 is unlimited)"));
        var down = new TextBox { Text = Settings.DownloadLimitKiB.ToString(CultureInfo.InvariantCulture), Padding = new(8), MaxLength = 7 };
        System.Windows.Automation.AutomationProperties.SetName(down, UiText.Translate("Download speed limit")); body.Children.Add(down);
        body.Children.Add(TrackingNote("Upload limit (KiB/s)"));
        var up = new TextBox { Text = Settings.UploadLimitKiB.ToString(CultureInfo.InvariantCulture), Padding = new(8), MaxLength = 7 };
        System.Windows.Automation.AutomationProperties.SetName(up, UiText.Translate("Upload speed limit")); body.Children.Add(up);
        var note = TrackingNote(""); body.Children.Add(note);
        var save = new Button { Content = UiText.Translate("Save"), Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        bool saved = false;
        save.Click += (_, _) =>
        {
            if (!int.TryParse(down.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int download) || download is < 0 or > 1048576 ||
                !int.TryParse(up.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int upload) || upload is < 1 or > 1048576 ||
                !Path.IsPathFullyQualified(path.Text) || Library.IsIncompletePath(path.Text))
            { note.Text = UiText.Translate("Choose a download folder and valid speed limits. Upload must be at least 1 KiB/s."); return; }
            try { torrentEngine.SetDownloadRoot(path.Text); }
            catch (Exception error) when (DownloadError(error)) { note.Text = UiText.Translate("This download folder is unavailable. Choose another folder."); return; }
            Settings.DownloadFolder = Path.GetFullPath(path.Text); Settings.DownloadLimitKiB = download; Settings.UploadLimitKiB = upload;
            EnsureDownloadLibraryRoot(); SaveSettings(); saved = true; dialog.Close();
        };
        body.Children.Add(save); dialog.ShowDialog();
        if (saved) { await ApplyDownloadLimits(); RenderDownloads(); _ = RefreshLibrary(); }
    }

    private async void SearchDownloadReleases(object sender, RoutedEventArgs e) => await SearchNyaa();
    private async void DownloadSearchKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchNyaa(); } }
    private async Task SearchNyaa()
    {
        feedRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(downloadLifetime.Token); feedRequest = request;
        DownloadSearchButton.IsEnabled = false; releaseEntries = []; FilterDownloadReleases();
        feedMessage = "Searching Nyaa…"; RenderDownloads();
        try
        {
            Uri source = DownloadSources.NyaaFeed(DownloadSearch.Text, (DownloadCategory.SelectedItem as DownloadChoice)?.Key ?? "1_2");
            downloadHttp ??= ReleaseFeedClient.CreateHttpClient();
            var entries = await new ReleaseFeedClient(downloadHttp).FetchAsync(source.AbsoluteUri, request.Token);
            if (request.IsCancellationRequested || feedRequest != request || closing) return;
            releaseEntries = entries; FilterDownloadReleases();
            if (entries.Count == 0) feedMessage = "No releases found. Try a different title or anime category.";
        }
        catch (OperationCanceledException) { if (!request.IsCancellationRequested) feedMessage = "Nyaa did not respond in time. Try again later."; }
        catch (Exception error) when (DownloadError(error)) { if (!request.IsCancellationRequested) feedMessage = "Nyaa could not be reached or returned an unsupported feed. You can still add a magnet or torrent file."; }
        finally { if (feedRequest == request) { feedRequest = null; DownloadSearchButton.IsEnabled = true; RenderDownloads(); } }
    }
    private void DownloadQualityChanged(object sender, SelectionChangedEventArgs e)
    { if (downloadsReady) { DownloadQuality.ToolTip = (DownloadQuality.SelectedItem as DownloadChoice)?.Name; FilterDownloadReleases(); RenderDownloads(); } }
    private void DownloadCategoryChanged(object sender, SelectionChangedEventArgs e)
    { if (downloadsReady) DownloadCategory.ToolTip = (DownloadCategory.SelectedItem as DownloadChoice)?.Name; }
    private void FilterDownloadReleases()
    {
        string quality = (DownloadQuality.SelectedItem as DownloadChoice)?.Key ?? "";
        DownloadReleases.ItemsSource = releaseEntries.Where(x => quality.Length == 0 || DownloadSources.Quality(x.Title) == quality).Select(x => new ReleaseRow(x)).ToArray();
        if (releaseEntries.Count > 0) feedMessage = DownloadReleases.Items.Count == 0 ? "No releases match this quality filter." :
            "Choose a release to download. Quality and language depend on the selected release.";
    }

    private async void DownloadRelease(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReleaseRow row } button) return;
        button.IsEnabled = false;
        try
        {
            downloadHttp ??= ReleaseFeedClient.CreateHttpClient();
            var uri = row.Entry.DownloadUri ?? await new ReleaseFeedClient(downloadHttp).ResolveNyaaAsync(row.Entry, downloadLifetime.Token);
            if (uri.Scheme == "magnet") await QueueDownload(token => torrentEngine.AddMagnetAsync(uri.AbsoluteUri, token));
            else
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(downloadLifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await downloadHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidOperationException();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var data = new MemoryStream();
                var buffer = new byte[8192]; int length;
                while ((length = await stream.ReadAsync(buffer, deadline.Token)) > 0)
                { if (data.Length + length > 4 * 1024 * 1024) throw new InvalidOperationException(); data.Write(buffer, 0, length); }
                byte[] metadata = data.ToArray(); await QueueDownload(token => torrentEngine.AddTorrentAsync(metadata, token));
            }
        }
        catch (OperationCanceledException) { if (!closing) { feedMessage = "The release could not be resolved. Try again or add its magnet link."; RenderDownloads(); } }
        catch (Exception error) when (DownloadError(error)) { feedMessage = "The release could not be resolved. Try again or add its magnet link."; RenderDownloads(); }
        finally { button.IsEnabled = true; }
    }

    private static string DownloadSize(long bytes)
    {
        double value = Math.Max(0, bytes); string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
    private static bool DownloadError(Exception error) => error is IOException or UnauthorizedAccessException or HttpRequestException or
        InvalidOperationException or ArgumentException or CryptographicException or System.Xml.XmlException or System.ComponentModel.Win32Exception or NotSupportedException;

    private void RememberDownloadPublications()
    {
        foreach (var job in torrentEngine.GetSnapshot().Where(x => x.State == "Completed"))
        {
            var marker = Library.Identify(Path.Combine(job.Destination, "publication"), Settings.Folders);
            if (marker.FolderId is not { } id || Path.GetDirectoryName(marker.Relative) is not { Length: > 0 } relative) continue;
            Settings.ManagedDownloadFolders.Add(new(id, relative));
        }
        Settings.ManagedDownloadFolders = DownloadSources.NormalizeFolders(Settings.ManagedDownloadFolders);
        if (Settings.ManagedDownloadFolders.Count > 0) SaveSettings();
    }

    private async Task RefreshAfterDownload()
    {
        // A scan already underway may have walked the staging parent before publication.
        while (scanning is not null && !closing) await Task.Delay(100);
        if (closing) return;
        await RefreshLibrary();
        downloadMessage = "Download complete. Your library has been refreshed.";
    }
}
