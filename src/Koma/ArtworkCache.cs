using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Koma;

internal sealed class ArtworkCache(string directory, string engine) : IDisposable
{
    private readonly SemaphoreSlim worker = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource generation = new();
    private readonly HashSet<string> failed = [];
    public bool Suspended { get; private set; }

    public bool SetPlayback(bool active)
    {
        if (active == Suspended) return false;
        Suspended = active;
        if (active) generation.Cancel();
        else { generation.Dispose(); generation = new(); }
        return true;
    }

    public async Task<BitmapSource?> Load(Collection collection, CancellationToken cancellation)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        try
        {
            string? source = collection.Seasons.FirstOrDefault()?.Episodes.FirstOrDefault()?.Path;
            foreach (string cover in ArtworkPolicy.Covers(collection.Directory, source is null ? null : Path.GetDirectoryName(source)))
            {
                var image = await Task.Run(() => ReadImage(cover), request.Token);
                if (image is not null) return image;
            }
            if (source is null || !File.Exists(source)) return null;
            string key = ArtworkPolicy.Key(source);
            string cached = Path.Combine(directory, key + ".jpg");
            var existing = await Task.Run(() => ReadImage(cached, true), request.Token);
            if (existing is not null) return existing;
            if (Suspended) return null;
            using var extraction = CancellationTokenSource.CreateLinkedTokenSource(request.Token, generation.Token);
            await worker.WaitAsync(extraction.Token);
            try
            {
                if (failed.Contains(key)) return null;
                existing = await Task.Run(() => ReadImage(cached, true), extraction.Token);
                if (existing is not null) return existing;
                bool written = await Extract(source, cached, extraction.Token);
                if (!written) { failed.Add(key); return null; }
                await Task.Run(Trim, extraction.Token);
                return await Task.Run(() => ReadImage(cached, true), extraction.Token);
            }
            finally { worker.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static BitmapSource? ReadImage(string path, bool touch = false)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 640;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            try { if (touch) File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        { return null; }
    }

    private async Task<bool> Extract(string source, string destination, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--no-config", "--load-scripts=no", "--no-audio", "--sid=no", "--access-references=no",
                "--no-ytdl", "--no-terminal", "--vo=image", "--frames=1", "--start=10%", "--vf=scale=640:-2",
                "--vo-image-format=jpg", "--vo-image-outdir=" + scratch, "--", source }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task output = process.StandardOutput.ReadToEndAsync();
            Task errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return false;
            }
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            string? frame = Directory.EnumerateFiles(scratch, "*.jpg").FirstOrDefault();
            if (process.ExitCode != 0 || frame is null) return false;
            File.Move(frame, destination, true);
            return true;
        }
        finally
        {
            // This directory is created above with a unique name inside this cache.
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Trim()
    {
        var files = new DirectoryInfo(directory).GetFiles("*.jpg").OrderBy(x => x.LastAccessTimeUtc).ToArray();
        long total = files.Sum(x => x.Length);
        foreach (var file in files)
        {
            if (total <= 250L * 1024 * 1024) break;
            total -= file.Length;
            file.Delete();
        }
    }

    public void Dispose() { lifetime.Cancel(); generation.Cancel(); }
}
