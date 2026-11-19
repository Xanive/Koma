using System.Text.Json;
using Koma;

internal static class TrackingChecks
{
    public static void Run(string scratch)
    {
        var watched = new WatchCoverage();
        for (int second = 0; second <= 80; second++) Sample(watched, second, second);
        var progress = watched.Snapshot();
        Require(progress.IsComplete && Near(progress.CoveredSeconds, 80) && Near(progress.Fraction, .8),
            "Eighty seconds of actual viewing must complete a hundred-second episode.");

        watched.Discontinuity();
        for (int second = 0; second <= 50; second++) Sample(watched, second, 100 + second);
        Require(Near(watched.Snapshot().CoveredSeconds, 80), "Rewatching the same interval must not double-count viewing.");
        progress.Ranges.Clear();
        Require(Near(watched.Snapshot().CoveredSeconds, 80), "Snapshots must not expose mutable tracker storage.");

        var skipped = new WatchCoverage();
        Sample(skipped, 0, 0);
        Sample(skipped, 1, 1);
        Sample(skipped, 98, 2);
        Sample(skipped, 99, 3);
        Require(!skipped.Snapshot().IsComplete && Near(skipped.Snapshot().CoveredSeconds, 2),
            "Seeking to the credits must not count the skipped episode.");
        skipped.Discontinuity();
        Sample(skipped, 10, 4);
        Sample(skipped, 11, 5);
        skipped.Break();
        Sample(skipped, 12, 6);
        Require(Near(skipped.Snapshot().CoveredSeconds, 3), "An explicit seek must break even a superficially plausible sample chain.");

        var pauses = new WatchCoverage();
        Sample(pauses, 0, 0);
        Sample(pauses, 1, 1);
        Sample(pauses, 50, 2, paused: true);
        Sample(pauses, 60, 3, paused: true);
        Sample(pauses, 61, 4);
        Sample(pauses, 62, 5);
        Sample(pauses, 80, 30);
        Sample(pauses, 81, 31);
        Require(Near(pauses.Snapshot().CoveredSeconds, 3), "Paused movement and long missing-sample gaps must not credit unseen content.");

        var sped = new WatchCoverage();
        for (int second = 0; second <= 40; second++) Sample(sped, second * 2, second, speed: 2);
        Require(sped.Snapshot().IsComplete && Near(sped.Snapshot().CoveredSeconds, 80),
            "Valid double-speed playback must count watched media time.");

        var partial = new WatchedEpisode(100, [new(0, 40)], DateTimeOffset.UtcNow, 321, 123456);
        var resumed = new WatchCoverage(JsonSerializer.Deserialize<WatchedEpisode>(JsonSerializer.Serialize(partial)));
        for (int second = 40; second <= 80; second++) Sample(resumed, second, second);
        Require(resumed.Snapshot() is { IsComplete: true, FileLength: 321, ModifiedUtcTicks: 123456 },
            "Saved unique intervals and file stamps must survive a session boundary.");
        resumed.Sample(0, 200, false, 1, TimeSpan.FromSeconds(200));
        Require(!resumed.Snapshot().IsComplete && Near(resumed.Snapshot().CoveredSeconds, 0),
            "A materially changed media duration must invalidate old viewing evidence.");

        var invalid = new WatchCoverage();
        invalid.Sample(double.NaN, 100, false, 1, TimeSpan.Zero);
        invalid.Sample(0, double.PositiveInfinity, false, 1, TimeSpan.Zero);
        invalid.Sample(0, 100, false, 100, TimeSpan.Zero);
        invalid.Sample(1, 100, false, 1, TimeSpan.FromSeconds(-1));
        Require(!invalid.Snapshot().IsComplete && invalid.Snapshot().Ranges.Count == 0,
            "Invalid timestamps, duration, speed or clocks must not fabricate viewing evidence.");

        var malformed = new Dictionary<string, WatchedEpisode>
        {
            ["episode"] = new(100, [new(20, 40), new(0, 30), new(80, 120), new(-1, 40), new(double.NaN, 90), null!], DateTimeOffset.UtcNow),
            ["bad-duration"] = new(double.NaN, [], default),
            ["null"] = null!,
            ["bad\nkey"] = partial,
            ["empty-ranges"] = new(100, null!, default, -1, -1)
        };
        var clean = WatchTracking.Normalize(malformed);
        Require(clean.Count == 2 && Near(clean["episode"].CoveredSeconds, 60) && clean["episode"].Ranges.Count == 2,
            "Malformed persistence must recover, merge overlapping intervals and clamp to the real duration.");
        Require(clean["empty-ranges"].FileLength == 0 && clean["empty-ranges"].ModifiedUtcTicks == 0 &&
            clean.ContainsKey("EPISODE") && WatchTracking.Normalize(null).Count == 0,
            "Missing ranges and invalid file stamps must recover using case-insensitive local identities.");

        var newest = WatchTracking.Normalize(new()
        {
            ["show"] = partial with { Updated = DateTimeOffset.UtcNow.AddMinutes(-1) },
            ["SHOW"] = partial with { Ranges = [new(0, 90)], Updated = DateTimeOffset.UtcNow }
        });
        Require(newest.Count == 1 && newest["show"].IsComplete, "The newest case-insensitive viewing record must win.");
        var fragmented = new WatchCoverage(new(1000, Enumerable.Range(0, 600).Select(index => new WatchedRange(index, index + .5)).ToList(), default));
        Require(fragmented.Snapshot().Ranges.Count == WatchTracking.MaximumRanges &&
            Near(fragmented.Snapshot().CoveredSeconds, WatchTracking.MaximumRanges * .5),
            "Bounding fragmented evidence must discard intervals instead of inventing watched gaps.");

        Require(WatchTracking.ContiguousEpisodeProgress(4, [6]) == 4,
            "Watching episode six must not mark a missing episode five as watched.");
        Require(WatchTracking.ContiguousEpisodeProgress(4, [8, 6, 5, 6]) == 6,
            "Only the contiguous run after remote progress may advance.");
        Require(WatchTracking.ContiguousEpisodeProgress(8, [1, 2, 3], 5) == 8,
            "Local evidence or a lower catalogue episode total must never decrease account progress.");
        Require(WatchTracking.ContiguousEpisodeProgress(0, [-1, 0, 1, 2, 3], 2) == 2 &&
            WatchTracking.ContiguousEpisodeProgress(int.MaxValue, [1, 2]) == int.MaxValue,
            "Progress must respect known series totals and remain safe at integer limits.");
    }

    private static void Sample(WatchCoverage coverage, double position, double time, bool paused = false, double speed = 1) =>
        coverage.Sample(position, 100, paused, speed, TimeSpan.FromSeconds(time));

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .00001;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
