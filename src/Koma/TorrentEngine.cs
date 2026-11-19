using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

namespace Koma;

internal sealed record TorrentJobInfo(Guid Id, string Name, string Destination, string State, double Progress,
    long DownloadedBytes, long TotalBytes, long DownloadBytesPerSecond, long UploadBytesPerSecond, string? Error);

/// <summary>Lazy, user-started BitTorrent transfers. Unverified media stays outside the playable library.</summary>
internal sealed class TorrentEngine : IAsyncDisposable
{
    private const int MaximumMetadata = 4 * 1024 * 1024;
    private const int MaximumQueue = 64 * 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Koma.DownloadQueue.v1");
    private readonly string queueFile, cacheDirectory;
    private readonly bool localOnly;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private string downloadRoot;
    private ClientEngine? engine;
    private Task? monitor;
    private TorrentJobInfo[] snapshot = [];
    private int downloadKiB, uploadKiB = 1024;
    private int shuttingDown;
    private Task? shutdownTask;

    private sealed record SavedQueue(int Version, int DownloadKiB, int UploadKiB, List<SavedJob> Jobs);
    private sealed record SavedJob(Guid Id, string Root, string Name, string Magnet, byte[]? Metadata,
        string Identity, string State, double Progress, long TotalBytes, string Destination)
    {
        public bool Published { get; init; }
    }
    private sealed class Job(SavedJob saved)
    {
        public SavedJob Saved = saved;
        public TorrentManager? Manager;
        public CancellationTokenSource? Operation;
        public Task? Starting;
        public string? Error;
    }

    public event Action? Changed;
    public event Action<Guid>? Completed;
    public string? Notice { get; private set; }

    public TorrentEngine(string dataDirectory, string downloadRoot, bool localOnly = false)
    {
        this.localOnly = localOnly;
        // A missing drive must not prevent the offline player from starting.
        this.downloadRoot = NormalizeRoot(downloadRoot);
        queueFile = Path.Combine(dataDirectory, "private", "download-queue.bin");
        cacheDirectory = Path.Combine(dataDirectory, "cache", "torrent-engine");
        ReadQueue(); UpdateSnapshot();
    }

    public void SetDownloadRoot(string root) => downloadRoot = ValidateRoot(root);
    public IReadOnlyList<TorrentJobInfo> GetSnapshot() => Volatile.Read(ref snapshot);

    public async Task<TorrentJobInfo> AddMagnetAsync(string text, CancellationToken token = default)
    {
        if (!ReleaseSource.TryParseMagnet(text, out var safe, out _) || safe is null)
            throw new ArgumentException("Enter a valid BitTorrent magnet link.");
        var magnet = ParseEngineMagnet(safe.AbsoluteUri);
        return await AddAsync(magnet.Name, safe.AbsoluteUri, null, Identity(magnet.InfoHashes), magnet.Size ?? 0, token);
    }

    public async Task<TorrentJobInfo> AddTorrentAsync(string path, CancellationToken token = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumMetadata) throw new ArgumentException("Choose a valid torrent file under 4 MB.");
        return await AddTorrentAsync(await File.ReadAllBytesAsync(path, token), token);
    }

    public async Task<TorrentJobInfo> AddTorrentAsync(byte[] metadata, CancellationToken token = default)
    {
        var (torrent, sanitized) = ParseMetadata(metadata);
        return await AddAsync(torrent.Name, "", sanitized, Identity(torrent.InfoHashes), torrent.Size, token);
    }

    private async Task<TorrentJobInfo> AddAsync(string? name, string magnet, byte[]? metadata, string identity, long size, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfClosing();
            if (jobs.Values.FirstOrDefault(x => x.Saved.Identity == identity) is { } existing)
                return ToInfo(existing);
            if (jobs.Count >= 200 || jobs.Values.Sum(x => (long)(x.Saved.Metadata?.Length ?? 0)) + (metadata?.Length ?? 0) > 32 * 1024 * 1024)
                throw new InvalidOperationException("The download queue is full. Remove finished entries before adding more.");
            Guid id = Guid.NewGuid();
            string title = CleanLabel(name);
            string root = ValidateRoot(downloadRoot);
            var saved = new SavedJob(id, root, title, magnet, metadata, identity, "Queued", 0, size,
                Path.Combine(root, SafeFolder(title) + "-" + id.ToString("N")[..8]));
            var job = new Job(saved); jobs.Add(id, job);
            try { SaveQueue(); } catch { jobs.Remove(id); throw; }
            UpdateSnapshot(); return ToInfo(job);
        }
        finally { gate.Release(); NotifyChanged(); }
    }

    public async Task StartAsync(Guid id, CancellationToken token = default)
    {
        Task operation;
        await gate.WaitAsync(token);
        try
        {
            ThrowIfClosing(); var job = Find(id);
            if (job.Saved.State == "Completed" && !HasPublishedPartials(job)) return;
            if (job.Starting is { IsCompleted: false }) { operation = job.Starting; }
            else if (job.Manager?.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Starting or TorrentState.Hashing or TorrentState.FetchingHashes) return;
            else
            {
                job.Error = null; job.Operation?.Dispose();
                job.Operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                job.Operation.CancelAfter(TimeSpan.FromMinutes(5));
                job.Saved = job.Saved with { State = job.Saved.Published ? "Checking" : job.Saved.Metadata is null ? "Metadata" : "Starting" };
                operation = job.Starting = job.Saved.Published ? RepairPublishedAsync(job, job.Operation) : StartCoreAsync(job, job.Operation);
                UpdateSnapshot();
            }
        }
        finally { gate.Release(); NotifyChanged(); }
        await operation;
    }

    private async Task StartCoreAsync(Job job, CancellationTokenSource operation)
    {
        // Yield so the public Start method can release the mutation gate before metadata acquisition.
        await Task.Yield();
        try
        {
            await gate.WaitAsync(operation.Token);
            ClientEngine active;
            try { ThrowIfClosing(); active = EnsureEngine(); }
            finally { gate.Release(); }
            if (job.Saved.Metadata is null)
            {
                var magnet = ParseEngineMagnet(job.Saved.Magnet);
                var bytes = await active.DownloadMetadataAsync(magnet, operation.Token);
                var (torrent, sanitized) = ParseMetadata(bytes.ToArray());
                if (!torrent.InfoHashes.Contains(magnet.InfoHashes.V1OrV2)) throw new InvalidOperationException("Torrent metadata did not match the requested download.");
                await gate.WaitAsync(operation.Token);
                try
                {
                    CheckCurrent(job, operation);
                    job.Saved = job.Saved with { Metadata = sanitized, Name = CleanLabel(torrent.Name), TotalBytes = torrent.Size,
                        Destination = Path.Combine(job.Saved.Root, SafeFolder(CleanLabel(torrent.Name)) + "-" + job.Saved.Id.ToString("N")[..8]) };
                    SaveQueue();
                }
                finally { gate.Release(); }
            }
            await gate.WaitAsync(operation.Token);
            try
            {
                CheckCurrent(job, operation);
                var (torrent, _) = ParseMetadata(job.Saved.Metadata!);
                string stage = Stage(job);
                ValidateLocation(job.Saved.Root, stage); Directory.CreateDirectory(stage);
                if (job.Manager is null)
                    job.Manager = await active.AddAsync(torrent, stage, new TorrentSettingsBuilder {
                        CreateContainingDirectory = false, AllowDht = !localOnly, AllowPeerExchange = !localOnly,
                        MaximumConnections = 40, UploadSlots = 4
                    }.ToSettings());
                ValidateManagerFiles(job.Manager, stage);
                job.Saved = job.Saved with { State = "Starting" };
                await job.Manager.StartAsync(); SaveQueue(); UpdateSnapshot();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            await gate.WaitAsync();
            try { if (jobs.ContainsKey(job.Saved.Id) && job.Saved.State != "Completed") job.Saved = job.Saved with { State = "Paused" }; UpdateSnapshot(); }
            finally { gate.Release(); }
        }
        catch (Exception error) when (Expected(error))
        {
            await gate.WaitAsync();
            try
            {
                if (jobs.ContainsKey(job.Saved.Id))
                {
                    job.Saved = job.Saved with { State = "Error" };
                    job.Error = "The download could not start. Check its source and destination, then retry.";
                    if (job.Manager is { } manager) await StopManager(manager);
                    SaveQueue(); UpdateSnapshot();
                }
            }
            finally { gate.Release(); }
        }
        finally { NotifyChanged(); }
    }

    public async Task RecoverCompletedAsync(CancellationToken token = default)
    {
        Guid[] completed;
        await gate.WaitAsync(token);
        try { ThrowIfClosing(); completed = jobs.Values.Where(job => job.Saved.State == "Completed").Select(job => job.Saved.Id).ToArray(); }
        finally { gate.Release(); }
        foreach (Guid id in completed)
        {
            token.ThrowIfCancellationRequested();
            await StartAsync(id, token);
        }
    }

    private static bool HasPublishedPartials(Job job)
    {
        if (!job.Saved.Published || job.Saved.Metadata is null || !Directory.Exists(job.Saved.Destination)) return false;
        var (torrent, _) = ParseMetadata(job.Saved.Metadata);
        return torrent.Files.Any(file => File.Exists(Path.Combine(job.Saved.Destination, file.Path) + ".!mt"));
    }

    private async Task RepairPublishedAsync(Job job, CancellationTokenSource operation)
    {
        await Task.Yield();
        bool repaired = false;
        await gate.WaitAsync();
        try
        {
            CheckCurrent(job, operation);
            if (job.Saved.Metadata is null) throw new IOException("Missing completion metadata.");
            var (torrent, _) = ParseMetadata(job.Saved.Metadata);
            if (torrent.InfoHashes.V2 is not null && !torrent.CreatePieceHashes().HasV2Hashes)
                throw new IOException("Offline verification needs the torrent's v2 piece layers.");
            string destination = job.Saved.Destination;
            ValidateLocation(job.Saved.Root, destination); ValidateTree(destination);
            // This verifier cannot contact trackers, discover peers, listen, or transfer data.
            using var verifier = new ClientEngine(new EngineSettingsBuilder {
                CacheDirectory = Path.Combine(cacheDirectory, "publication-check"), UsePartialFiles = true,
                AllowPortForwarding = false, AllowLocalPeerDiscovery = false, DhtEndPoint = null, ListenEndPoints = new(),
                AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false,
                MaximumOpenFiles = 4, DiskCacheBytes = 1024 * 1024
            }.ToSettings());
            var manager = await verifier.AddAsync(torrent, destination, new TorrentSettingsBuilder {
                CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false
            }.ToSettings());
            try
            {
                ValidatePublicationFiles(manager, destination);
                await manager.HashCheckAsync(false).WaitAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!manager.HashChecked || !manager.Complete || manager.State != TorrentState.Stopped)
                    throw new IOException("The published files did not pass their torrent hashes.");
                // On this isolated verifier, toggling partial filenames awaits the
                // engine's rename work without affecting any active downloads.
                await verifier.UpdateSettingsAsync(new EngineSettingsBuilder(verifier.Settings) { UsePartialFiles = false }.ToSettings());
                await StopManager(manager);
                await verifier.RemoveAsync(manager);
                FinalizePublicationFiles(manager, destination);
                job.Saved = job.Saved with { State = "Completed", Progress = 100 }; job.Error = null;
                SaveQueue(); repaired = true;
            }
            finally { await StopManager(manager); }
        }
        catch (OperationCanceledException)
        { job.Saved = job.Saved with { State = "Completed" }; }
        catch (Exception error) when (Expected(error))
        {
            job.Saved = job.Saved with { State = "Error" };
            job.Error = "Completed files could not be verified for the library. Your files were kept. Choose Resume to retry.";
            try { SaveQueue(); } catch (Exception saveError) when (Expected(saveError)) { }
        }
        finally { UpdateSnapshot(); gate.Release(); NotifyChanged(); }
        if (repaired) NotifyCompleted(job.Saved.Id);
    }

    public async Task PauseAsync(Guid id, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfClosing(); var job = Find(id); job.Operation?.Cancel();
            if (job.Manager is { } manager) await StopManager(manager);
            if (job.Saved.State != "Completed") job.Saved = job.Saved with { State = "Paused" };
            SaveQueue(); UpdateSnapshot();
        }
        finally { gate.Release(); NotifyChanged(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfClosing(); var job = Find(id); job.Operation?.Cancel();
            if (job.Manager is { } manager)
            { await StopManager(manager); await engine!.RemoveAsync(manager); }
            jobs.Remove(id); SaveQueue(); UpdateSnapshot();
            // Both partial and completed files intentionally remain on disk.
        }
        finally { gate.Release(); NotifyChanged(); }
    }

    public async Task LimitsAsync(int downloadKiB, int uploadKiB, CancellationToken token = default)
    {
        if (downloadKiB is < 0 or > 1024 * 1024 || uploadKiB is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(downloadKiB));
        await gate.WaitAsync(token);
        try
        {
            ThrowIfClosing(); this.downloadKiB = downloadKiB; this.uploadKiB = uploadKiB;
            if (engine is not null) await engine.UpdateSettingsAsync(Settings()); SaveQueue();
        }
        finally { gate.Release(); }
    }

    private ClientEngine EnsureEngine()
    {
        if (engine is not null)
        {
            if (monitor is { IsCompleted: true } && !lifetime.IsCancellationRequested) monitor = MonitorAsync();
            return engine;
        }
        engine = new ClientEngine(Settings()); monitor = MonitorAsync(); return engine;
    }

    private EngineSettings Settings() => new EngineSettingsBuilder {
        CacheDirectory = cacheDirectory, AllowPortForwarding = false, AllowLocalPeerDiscovery = false,
        AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false,
        DhtEndPoint = localOnly ? null : new IPEndPoint(IPAddress.Any, 0),
        ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(localOnly ? IPAddress.Loopback : IPAddress.Any, 0) },
        MaximumConnections = 80, MaximumHalfOpenConnections = 5, MaximumOpenFiles = 20,
        MaximumDownloadRate = checked(downloadKiB * 1024), MaximumUploadRate = checked(uploadKiB * 1024),
        // Incomplete files are already isolated in .koma-incomplete. Avoid the
        // engine's asynchronous per-file suffix rename at completion.
        UsePartialFiles = false, DiskCacheBytes = 8 * 1024 * 1024
    }.ToSettings();

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                List<Guid> finished = [];
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    foreach (var job in jobs.Values)
                    {
                        if (job.Manager is not { } manager || job.Saved.State is "Paused" or "Completed" or "Error" or "Queued") continue;
                        if (manager.Complete && manager.State == TorrentState.Seeding)
                        {
                            try
                            {
                                await StopManager(manager);
                                string stage = Stage(job); ValidateLocation(job.Saved.Root, stage);
                                string destination = job.Saved.Destination;
                                ValidateLocation(job.Saved.Root, destination);
                                if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Destination already exists.");
                                ValidateTree(stage);
                                await engine!.RemoveAsync(manager); job.Manager = null;
                                FinalizePublicationFiles(manager, stage);
                                Directory.Move(stage, destination);
                                job.Saved = job.Saved with { State = "Completed", Progress = 100, Published = true }; job.Error = null;
                                finished.Add(job.Saved.Id);
                                try { SaveQueue(); }
                                catch (Exception error) when (Expected(error))
                                { Notice = "A download completed, but its saved queue could not be updated."; }
                            }
                            catch (Exception error) when (Expected(error))
                            { job.Saved = job.Saved with { State = "Error" }; job.Error = "The download finished but could not be added to the library. Check the destination and retry."; }
                        }
                        else if (manager.State == TorrentState.Error)
                        { job.Saved = job.Saved with { State = "Error" }; job.Error = "The download stopped. Check available disk space and the source, then retry."; }
                        else job.Saved = job.Saved with { State = State(manager.State), Progress = Math.Clamp(manager.Progress, 0, 100) };
                    }
                    UpdateSnapshot();
                }
                finally { gate.Release(); }
                NotifyChanged(); foreach (Guid id in finished) NotifyCompleted(id);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (Expected(error))
        {
            Notice = "Download monitoring stopped. Resume a paused download to retry.";
            await gate.WaitAsync();
            try
            {
                foreach (var job in jobs.Values.Where(x => x.Saved.State is not ("Queued" or "Paused" or "Completed" or "Error")))
                {
                    job.Operation?.Cancel();
                    try { if (job.Manager is { } manager) await StopManager(manager); }
                    catch (Exception stopError) when (Expected(stopError)) { }
                    job.Saved = job.Saved with { State = "Paused" };
                    job.Error = "Download monitoring stopped. Resume this download to retry.";
                }
                UpdateSnapshot();
                try { SaveQueue(); } catch (Exception saveError) when (Expected(saveError)) { }
            }
            finally { gate.Release(); }
            NotifyChanged();
        }
    }

    public Task ShutdownAsync()
    {
        lock (lifetime) return shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        Interlocked.Exchange(ref shuttingDown, 1); lifetime.Cancel();
        Task[] starting = []; Exception? saveFailure = null;
        try
        {
            await gate.WaitAsync();
            try
            {
                foreach (var job in jobs.Values) job.Operation?.Cancel();
                starting = jobs.Values.Where(x => x.Starting is not null).Select(x => x.Starting!).ToArray();
                try { if (engine is not null) await engine.StopAllAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (Expected(error)) { saveFailure = error; }
                foreach (var job in jobs.Values)
                    if (job.Saved.State is not ("Completed" or "Queued")) job.Saved = job.Saved with { State = "Paused" };
                try { SaveQueue(); }
                catch (Exception error) when (Expected(error)) { saveFailure = error; }
                UpdateSnapshot();
            }
            finally { gate.Release(); }
            try { await Task.WhenAll(starting).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (Expected(error) || error is OperationCanceledException) { }
            if (monitor is not null) await monitor;
        }
        finally { engine?.Dispose(); NotifyChanged(); }
        if (saveFailure is not null) throw new IOException("Downloads stopped, but the saved queue could not be updated.", saveFailure);
    }

    public async ValueTask DisposeAsync() { await ShutdownAsync(); lifetime.Dispose(); }

    private TorrentJobInfo ToInfo(Job job)
    {
        var saved = job.Saved; var manager = job.Manager;
        long total = manager?.Torrent?.Size ?? saved.TotalBytes;
        double progress = saved.State == "Completed" ? 100 : manager is not null && saved.State != "Paused" ? manager.Progress : saved.Progress;
        return new(saved.Id, saved.Name, saved.Destination, saved.State, Math.Clamp(progress, 0, 100),
            (long)(Math.Clamp(progress, 0, 100) / 100 * total), total,
            saved.State is "Paused" or "Completed" or "Error" ? 0 : manager?.Monitor.DownloadRate ?? 0,
            saved.State is "Paused" or "Completed" or "Error" ? 0 : manager?.Monitor.UploadRate ?? 0, job.Error);
    }
    private void UpdateSnapshot() => Volatile.Write(ref snapshot, jobs.Values.Select(ToInfo).ToArray());
    private static string State(TorrentState state) => state switch {
        TorrentState.Metadata => "Metadata", TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.FetchingHashes => "Checking",
        TorrentState.Starting => "Starting", TorrentState.Downloading or TorrentState.Seeding => "Downloading",
        TorrentState.Error => "Error", _ => "Paused"
    };
    private Job Find(Guid id) => jobs.GetValueOrDefault(id) ?? throw new ArgumentException("This download is no longer in the queue.");
    private void ThrowIfClosing() { if (Volatile.Read(ref shuttingDown) != 0) throw new ObjectDisposedException(nameof(TorrentEngine)); }
    private void CheckCurrent(Job job, CancellationTokenSource operation) { ThrowIfClosing(); operation.Token.ThrowIfCancellationRequested(); if (!jobs.ContainsKey(job.Saved.Id) || job.Operation != operation) throw new OperationCanceledException(); }
    private static async Task StopManager(TorrentManager manager) { if (manager.State != TorrentState.Stopped) await manager.StopAsync(TimeSpan.FromSeconds(2)); }
    private static string Stage(Job job) => Path.Combine(job.Saved.Root, ".koma-incomplete", job.Saved.Id.ToString("N"));
    private static string Identity(InfoHashes hashes) => (hashes.V1 ?? hashes.V2)!.ToHex();
    private static MagnetLink ParseEngineMagnet(string text)
    {
        if (!ReleaseSource.TryParseMagnet(text, out var safe, out _) || safe is null)
            throw new ArgumentException("Enter a valid BitTorrent magnet link.");
        // MonoTorrent 3.0.2 expects a literal xt URN prefix. Keep names and tracker
        // values escaped; decoding the whole URI could turn a name's '&' into a field.
        string query = string.Join('&', safe.AbsoluteUri[8..].Split('&').Select(part =>
            part.StartsWith("xt=", StringComparison.Ordinal) ? "xt=" + Uri.UnescapeDataString(part[3..]) : part));
        if (!MagnetLink.TryParse("magnet:?" + query, out var magnet))
            throw new ArgumentException("Enter a valid BitTorrent magnet link.");
        return magnet;
    }
    private static string CleanLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "Unnamed download" : new string(value.Where(c => !char.IsControl(c)).Take(240).ToArray());
    private static string SafeFolder(string name)
    {
        string clean = new(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).Take(100).ToArray());
        clean = clean.Trim(' ', '.'); if (clean.Length == 0 || !SafeComponent(clean)) clean = "Anime download"; return clean;
    }

    private static (Torrent Torrent, byte[] Bytes) ParseMetadata(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumMetadata) throw new ArgumentException("Choose a valid torrent file under 4 MB.");
        ValidateEncoding(bytes);
        try
        {
            var dictionary = BEncodedValue.Decode<BEncodedDictionary>(bytes);
            dictionary.Remove("url-list"); dictionary.Remove("httpseeds"); dictionary.Remove("nodes");
            var torrent = Torrent.Load(dictionary);
            if (!SafeComponent(torrent.Name) || torrent.Size < 0 || torrent.Files.Count is <= 0 or > 20000) throw new ArgumentException("This torrent has unsupported file paths.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in torrent.Files)
                if (file.Length < 0 || !SafeRelative(file.Path) || !paths.Add(file.Path.Replace('/', '\\'))) throw new ArgumentException("This torrent has unsupported file paths.");
            foreach (string tracker in torrent.AnnounceUrls.SelectMany(x => x))
                if (!Uri.TryCreate(tracker, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "udp") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
                    throw new ArgumentException("This torrent has an unsupported tracker address.");
            return (torrent, dictionary.Encode());
        }
        catch (Exception error) when (error is BEncodingException or TorrentException or FormatException or OverflowException or
            KeyNotFoundException or InvalidCastException or IndexOutOfRangeException or NullReferenceException)
        { throw new ArgumentException("This torrent could not be read."); }
    }

    private static void ValidateEncoding(byte[] bytes)
    {
        int index = 0, nodes = 0;
        Read(0);
        if (index != bytes.Length || bytes[0] != (byte)'d') throw new ArgumentException("This torrent could not be read.");
        void Read(int depth)
        {
            if (depth > 32 || index >= bytes.Length || ++nodes > 200000) throw new ArgumentException("This torrent could not be read.");
            byte first = bytes[index++];
            if (first is (byte)'d' or (byte)'l')
            {
                while (index < bytes.Length && bytes[index] != (byte)'e') Read(depth + 1);
                if (index >= bytes.Length) throw new ArgumentException("This torrent could not be read."); index++; return;
            }
            if (first == (byte)'i')
            {
                int start = index;
                while (index < bytes.Length && bytes[index] != (byte)'e')
                { if (index - start > 22 || bytes[index] != (byte)'-' && (bytes[index] < (byte)'0' || bytes[index] > (byte)'9')) throw new ArgumentException("This torrent could not be read."); index++; }
                if (index == start || index >= bytes.Length) throw new ArgumentException("This torrent could not be read."); index++; return;
            }
            if (first < (byte)'0' || first > (byte)'9') throw new ArgumentException("This torrent could not be read.");
            long length = first - (byte)'0'; int digits = 1;
            while (index < bytes.Length && bytes[index] != (byte)':')
            { if (++digits > 9 || bytes[index] < (byte)'0' || bytes[index] > (byte)'9') throw new ArgumentException("This torrent could not be read."); length = length * 10 + bytes[index++] - (byte)'0'; }
            if (index >= bytes.Length || length > bytes.Length - index - 1) throw new ArgumentException("This torrent could not be read.");
            index += 1 + (int)length;
        }
    }

    private static bool SafeRelative(string path) => path.Length is > 0 and < 2000 && !Path.IsPathRooted(path) &&
        path.Replace('\\', '/').Split('/').All(SafeComponent);
    private static bool SafeComponent(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 240 || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        if (name.Equals(".koma-incomplete", StringComparison.OrdinalIgnoreCase)) return false;
        string first = name.Split('.')[0].TrimEnd(' ').Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return first is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") &&
            !(first.Length == 4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && first[3] is >= '0' and <= '9');
    }
    private static string ValidateRoot(string root)
    {
        string full = NormalizeRoot(root); RefuseReparseAncestors(full); return full;
    }
    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.Any(char.IsControl)) throw new ArgumentException("Choose an absolute download folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    private static void ValidateSavedLocation(string root, string path)
    {
        root = NormalizeRoot(root); string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, full);
        if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Download path escaped its selected root.");
    }
    private static void ValidateLocation(string root, string path)
    {
        root = ValidateRoot(root); string full = Path.GetFullPath(path);
        ValidateSavedLocation(root, full);
        RefuseReparseAncestors(full);
    }
    private static void RefuseReparseAncestors(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if ((item.Exists || File.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a download folder without junctions or symbolic links.");
    }
    private static void ValidateManagerFiles(TorrentManager manager, string stage)
    {
        foreach (var file in manager.Files) ValidateLocation(stage, file.FullPath);
    }
    private static void ValidatePublicationFiles(TorrentManager manager, string root)
    {
        foreach (var file in manager.Files)
        {
            string complete = file.DownloadCompleteFullPath, partial = file.DownloadIncompleteFullPath;
            ValidateLocation(root, complete); ValidateLocation(root, partial);
            bool completeExists = File.Exists(complete), partialExists = File.Exists(partial);
            if (!string.Equals(complete, partial, StringComparison.OrdinalIgnoreCase) && completeExists && partialExists)
                throw new IOException("Both the final and temporary download filenames exist.");
            string existing = completeExists ? complete : partial;
            if ((!completeExists && !partialExists) || new FileInfo(existing).Length != file.Length)
                throw new IOException("A completed download file is missing or has an unexpected size.");
        }
    }
    private static void FinalizePublicationFiles(TorrentManager manager, string root)
    {
        // Seeding and Complete do not await MonoTorrent's background .!mt renames.
        // Call only after verified completion, stopping and removing the manager.
        ValidatePublicationFiles(manager, root);
        foreach (var file in manager.Files)
        {
            string complete = file.DownloadCompleteFullPath, partial = file.DownloadIncompleteFullPath;
            if (!File.Exists(complete))
            {
                try { File.Move(partial, complete); }
                catch (IOException) when (!File.Exists(partial) && File.Exists(complete) && new FileInfo(complete).Length == file.Length) { }
            }
        }
        if (manager.Files.Any(file => !File.Exists(file.DownloadCompleteFullPath)))
            throw new IOException("Completed download filenames were not finalized.");
    }
    private static void ValidateTree(string root)
    {
        RefuseReparseAncestors(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Download staging contains an unsupported link.");
            if (Directory.Exists(path)) ValidateTree(path);
        }
    }

    private void ReadQueue()
    {
        foreach (string file in new[] { queueFile, queueFile + ".bak" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                if (new FileInfo(file).Length > MaximumQueue) throw new IOException();
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    var queue = JsonSerializer.Deserialize<SavedQueue>(clear);
                    if (queue is not { Version: 1 } || queue.Jobs is null || queue.Jobs.Count > 200) throw new JsonException();
                    foreach (var saved in queue.Jobs)
                    {
                        if (saved is null || saved.Id == Guid.Empty || jobs.ContainsKey(saved.Id) || saved.Identity is null || saved.Identity.Length is not (40 or 64) ||
                            saved.Name is null || saved.Name.Length > 240 || saved.Metadata?.Length > MaximumMetadata || saved.Magnet?.Length > 32768 || saved.Destination is null) continue;
                        ValidateSavedLocation(saved.Root, saved.Destination);
                        if (saved.Metadata is { } metadata) ParseMetadata(metadata);
                        else if (!ReleaseSource.TryParseMagnet(saved.Magnet, out _, out _)) continue;
                        bool published = saved.Published || saved.State == "Completed";
                        jobs[saved.Id] = new(saved with { Published = published,
                            State = saved.State == "Completed" ? "Completed" : published ? "Error" : "Paused",
                            Progress = double.IsFinite(saved.Progress) ? Math.Clamp(saved.Progress, 0, 100) : 0 }) {
                                Error = published && saved.State != "Completed" ?
                                    "Completed files could not be verified for the library. Your files were kept. Choose Resume to retry." : null
                            };
                    }
                    downloadKiB = Math.Clamp(queue.DownloadKiB, 0, 1024 * 1024); uploadKiB = Math.Clamp(queue.UploadKiB, 1, 1024 * 1024); return;
                }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (Exception error) when (Expected(error))
            {
                jobs.Clear(); Notice = "Saved downloads could not be read. Existing files were kept; the queue needs to be added again.";
                // Retain the encrypted original before any later preference/limit save creates a fresh queue.
                try { File.Move(file, file + ".damaged-" + Guid.NewGuid().ToString("N")); }
                catch (Exception preserveError) when (Expected(preserveError)) { }
            }
        }
    }

    private void SaveQueue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(queueFile)!);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new SavedQueue(1, downloadKiB, uploadKiB, jobs.Values.Select(x => x.Saved).ToList()));
        string temporary = queueFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (clear.Length > MaximumQueue) throw new IOException("The saved download queue is too large.");
            File.WriteAllBytes(temporary, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
            if (File.Exists(queueFile)) File.Replace(temporary, queueFile, queueFile + ".bak", true); else File.Move(temporary, queueFile);
        }
        finally { CryptographicOperations.ZeroMemory(clear); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        CryptographicException or JsonException or TorrentException or TimeoutException or NotSupportedException;

    private void NotifyChanged()
    {
        foreach (Action callback in Changed?.GetInvocationList() ?? [])
            try { callback(); } catch (Exception error) when (Expected(error)) { }
    }
    private void NotifyCompleted(Guid id)
    {
        foreach (Action<Guid> callback in Completed?.GetInvocationList() ?? [])
            try { callback(id); } catch (Exception error) when (Expected(error)) { }
    }
}
