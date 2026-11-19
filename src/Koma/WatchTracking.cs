using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Koma;

internal sealed record WatchedRange(double Start, double End);

/// <summary>Local viewing evidence. This is separate from a resume position or an end-of-file event.</summary>
internal sealed record WatchedEpisode(
    double Duration,
    List<WatchedRange> Ranges,
    DateTimeOffset Updated,
    long FileLength = 0,
    long ModifiedUtcTicks = 0)
{
    [JsonIgnore] public double CoveredSeconds => Ranges.Sum(range => Math.Max(0, range.End - range.Start));
    [JsonIgnore] public double Fraction => Duration > 0 ? Math.Clamp(CoveredSeconds / Duration, 0, 1) : 0;
    [JsonIgnore] public bool IsComplete => Duration > 0 && Fraction >= WatchTracking.CompletionFraction;
}

/// <summary>Counts unique media intervals only when consecutive samples resemble actual playback.</summary>
internal sealed class WatchCoverage
{
    private WatchedEpisode progress;
    private PlaybackSample? previous;

    public WatchCoverage(WatchedEpisode? existing = null) => progress = WatchTracking.Clean(existing) ?? Empty();

    public void Reset(WatchedEpisode? existing = null)
    {
        progress = WatchTracking.Clean(existing) ?? Empty();
        Discontinuity();
    }

    /// <summary>Call before an explicit seek, load, or playback-state change.</summary>
    public void Discontinuity() => previous = null;

    public void Break() => Discontinuity();

    /// <summary>
    /// monotonicTime is an elapsed time from a single monotonic clock, such as Stopwatch.GetElapsedTime.
    /// Use genuine time-position notifications, and break the chain for explicit seeks.
    /// Returns true only when stored viewing evidence changed.
    /// </summary>
    public bool Sample(double position, double duration, bool paused, double speed, TimeSpan monotonicTime)
    {
        if (!double.IsFinite(position) || !WatchTracking.ValidDuration(duration) || position < 0 ||
            position > duration + .5 || !double.IsFinite(speed) || speed is < .1 or > 16 ||
            monotonicTime < TimeSpan.Zero)
        {
            Discontinuity();
            return false;
        }

        bool changed = false;
        // A materially different duration indicates different media or incomplete metadata at load time.
        if (progress.Duration <= 0 || Math.Abs(progress.Duration - duration) > Math.Max(2, duration * .01))
        {
            progress = progress with { Duration = duration, Ranges = [], Updated = DateTimeOffset.UtcNow };
            previous = null;
            changed = true;
        }
        else if (Math.Abs(progress.Duration - duration) > .001)
        {
            progress = progress with { Duration = duration, Ranges = WatchTracking.Merge(progress.Ranges, duration) };
            changed = true;
        }

        var current = new PlaybackSample(Math.Min(position, duration), paused, speed, monotonicTime);
        var before = previous;
        previous = current;
        if (before is null || before.Paused || paused || Math.Abs(before.Speed - speed) > .000001)
            return changed;

        double elapsed = (monotonicTime - before.Time).TotalSeconds;
        double advanced = current.Position - before.Position;
        // Long gaps (sleep, suspension, lost events) and unexpected jumps are not proof of viewing.
        if (elapsed <= 0 || elapsed > 5 || advanced <= 0 ||
            advanced > elapsed * speed + Math.Max(.25, elapsed * speed * .20))
            return changed;

        var ranges = WatchTracking.Merge(progress.Ranges.Append(new(before.Position, current.Position)), duration);
        if (ranges.SequenceEqual(progress.Ranges)) return changed;
        progress = progress with { Ranges = ranges, Updated = DateTimeOffset.UtcNow };
        return true;
    }

    public WatchedEpisode Snapshot() => progress with { Ranges = [.. progress.Ranges] };

    private static WatchedEpisode Empty() => new(0, [], DateTimeOffset.UtcNow);
    private sealed record PlaybackSample(double Position, bool Paused, double Speed, TimeSpan Time);
}

internal static class WatchTracking
{
    public const double CompletionFraction = .80;
    public const int MaximumRecords = 10_000;
    public const int MaximumRanges = 256;
    private const int MaximumEpisode = 100_000;

    public static Dictionary<string, WatchedEpisode> Normalize(Dictionary<string, WatchedEpisode>? records)
    {
        var normalized = new Dictionary<string, WatchedEpisode>(StringComparer.OrdinalIgnoreCase);
        if (records is null) return normalized;
        foreach (var (key, value) in records.OrderByDescending(pair => pair.Value?.Updated ?? default))
        {
            if (normalized.Count >= MaximumRecords) break;
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl)) continue;
            var cleaned = Clean(value);
            if (cleaned is not null) normalized.TryAdd(key, cleaned);
        }
        return normalized;
    }

    /// <summary>Never reduces an account count and never jumps over an episode with no local viewing evidence.</summary>
    public static int ContiguousEpisodeProgress(int remoteCount, IEnumerable<int> completed, int? episodeTotal = null)
    {
        int count = Math.Max(0, remoteCount);
        if (count >= MaximumEpisode) return count;
        int maximum = episodeTotal is > 0 ? Math.Max(count, Math.Min(MaximumEpisode, episodeTotal.Value)) : MaximumEpisode;
        var seen = completed.Where(number => number > count && number <= maximum).ToHashSet();
        while (count < maximum && seen.Contains(count + 1)) count++;
        return count;
    }

    internal static WatchedEpisode? Clean(WatchedEpisode? episode)
    {
        if (episode is null || !ValidDuration(episode.Duration)) return null;
        var ranges = Merge(episode.Ranges ?? [], episode.Duration);
        return episode with
        {
            Ranges = ranges,
            FileLength = Math.Max(0, episode.FileLength),
            ModifiedUtcTicks = episode.ModifiedUtcTicks is >= 0 and <= 3155378975999999999 ? episode.ModifiedUtcTicks : 0
        };
    }

    internal static bool ValidDuration(double duration) => double.IsFinite(duration) && duration is >= 1 and <= 86400;

    internal static List<WatchedRange> Merge(IEnumerable<WatchedRange> input, double duration)
    {
        var merged = new List<WatchedRange>();
        foreach (var range in input.Where(range => range is not null && double.IsFinite(range.Start) && double.IsFinite(range.End) &&
                     range.Start >= 0 && range.End > range.Start && range.Start < duration)
                 .Select(range => new WatchedRange(range.Start, Math.Min(range.End, duration)))
                 .OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = new(merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        // Keep the largest genuine intervals. Never merge unseen gaps simply to shrink the file.
        return merged.Count <= MaximumRanges ? merged : merged.OrderByDescending(range => range.End - range.Start)
            .Take(MaximumRanges).OrderBy(range => range.Start).ToList();
    }
}
