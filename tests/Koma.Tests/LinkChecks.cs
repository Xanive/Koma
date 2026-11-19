using Koma;

internal static class LinkChecks
{
    public static void Run(string scratch)
    {
        Guid root = Guid.NewGuid();
        var frieren = new DetectedAnime("Frieren", 1, 2, AnimeKind.Episode, false);
        string originalPath = Path.Combine(scratch, "old-root", "Frieren", "Season 1", "Episode 02.mkv");
        string relocatedPath = Path.Combine(scratch, "new-root", "Frieren", "Season 1", "Episode 02.mkv");
        var before = new Episode(originalPath, root, @"Frieren\Season 1\Episode 02.mkv");
        var after = new Episode(relocatedPath, root, "Frieren/Season 1/Episode 02.mkv");
        string key = AnimeLinkKeys.For(before, frieren);
        Require(key.Length > 0 && key == AnimeLinkKeys.For(after, frieren),
            "A saved root relocation or separator choice must preserve its remembered match.");
        Require(!key.Contains("old-root", StringComparison.OrdinalIgnoreCase) && !key.Contains(scratch, StringComparison.OrdinalIgnoreCase),
            "A saved-root mapping must not retain its absolute workstation path.");

        var nextEpisode = before with { Relative = @"Frieren\Season 1\Episode 10.mkv" };
        Require(key == AnimeLinkKeys.For(nextEpisode, frieren with { Episode = 10 }),
            "Episodes within the same show and season must share a remembered match.");
        Require(key != AnimeLinkKeys.For(before, frieren with { Season = 2 }), "A second season must not reuse the first-season mapping.");
        Require(key != AnimeLinkKeys.For(before, frieren with { Kind = AnimeKind.Special }), "Specials must not reuse an ordinary-episode mapping.");
        Require(key != AnimeLinkKeys.For(before with { FolderId = Guid.NewGuid() }, frieren), "Independent saved roots need separate mappings.");

        var loose = before with { Relative = "Episode 02.mkv" };
        Require(AnimeLinkKeys.For(loose, frieren) != AnimeLinkKeys.For(loose, frieren with { Title = "Delicious in Dungeon" }),
            "Different loose shows kept in one directory must not share a mapping.");
        Require(AnimeLinkKeys.For(loose, frieren with { Title = "86" }) != AnimeLinkKeys.For(loose, frieren with { Title = "87" }),
            "Numeric show titles must remain distinct.");

        var unsaved = new Episode(originalPath, null, originalPath);
        Require(AnimeLinkKeys.For(unsaved, frieren) != AnimeLinkKeys.For(unsaved with { Path = relocatedPath }, frieren),
            "Unsaved-file mappings must be scoped to their absolute parent directory.");
        Require(AnimeLinkKeys.For(unsaved, frieren).Contains("old-root", StringComparison.Ordinal),
            "Unsaved locations need an explicit local absolute-parent fallback.");
        Require(key == AnimeLinkKeys.For(before with { Relative = @"Frieren\.\Season 1\Episode 02.mkv" }, frieren),
            "Equivalent relative parent paths must have the same mapping key.");
        Require(AnimeLinkKeys.For(before with { Relative = @"..\outside\Episode 02.mkv" }, frieren).Length == 0,
            "A relative path escaping its saved root cannot supply a stable saved-root identity.");
        Require(AnimeLinkKeys.For(before, frieren with { Title = "" }).Length == 0, "Missing titles must not create a shared mapping.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var matched = new AnimeLink(52991, true, "Frieren", now);
        var disabled = new AnimeLink(0, true, "Delicious in Dungeon", now);
        var input = new Dictionary<string, AnimeLink>
        {
            [key] = matched,
            ["disabled|mapping"] = disabled,
            ["automatic|mapping"] = new(123, false, "Show Season 2", now),
            ["negative"] = new(-1, true, "Show", now),
            ["missing-title"] = matched with { LocalTitle = " " },
            ["too-long-title"] = matched with { LocalTitle = new string('a', 241) },
            ["control-title"] = matched with { LocalTitle = "Show\nOther" },
            ["missing-value"] = null!,
            [""] = matched,
            [" "] = matched,
            ["bad\nkey"] = matched,
            [new string('a', 5000)] = matched
        };
        var normalized = AnimeLinkKeys.Normalize(input);
        Require(normalized.Count == 3 && normalized[key] == matched && normalized["disabled|mapping"] == disabled,
            "Normalization must preserve valid matches and disabled sentinels while rejecting malformed entries.");
        Require(normalized.Comparer.Equals(StringComparer.OrdinalIgnoreCase) && normalized.ContainsKey(key.ToUpperInvariant()),
            "Remembered mapping lookups must use Windows-compatible case-insensitive keys.");
        Require(normalized["automatic|mapping"].LocalTitle == "Show Season 2" && !normalized["automatic|mapping"].Confirmed,
            "Local title numbers and unconfirmed automatic-match state must survive normalization.");
        Require(AnimeLinkKeys.Normalize(null).Count == 0, "A missing mapping collection must recover as an empty collection.");

        var collisions = new Dictionary<string, AnimeLink>
        {
            ["same-key"] = matched,
            ["SAME-KEY"] = matched with { MalId = 0, Updated = now.AddSeconds(1) }
        };
        Require(AnimeLinkKeys.Normalize(collisions)["same-key"].MalId == 0,
            "The newest user-disabled mapping must win a case-insensitive collision.");

        string profile = Path.Combine(scratch, "recognition-profile");
        var store = new PreferenceStore(profile, Path.Combine(scratch, "no-legacy.json"));
        Require(!store.Data.OnlineAnimeRecognition, "Online matching must be opt-in for existing and new profiles.");
        store.Data.AnimeLinks = normalized;
        store.Data.OnlineAnimeRecognition = true;
        store.Save();
        var restored = new PreferenceStore(profile, Path.Combine(scratch, "no-legacy.json"));
        Require(restored.Data.OnlineAnimeRecognition && restored.Data.AnimeLinks[key] == matched &&
            restored.Data.AnimeLinks["disabled|mapping"] == disabled,
            "Confirmed links, automatic links and disabled choices must persist through actual settings storage.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
