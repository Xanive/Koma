using Koma;

internal static class RecognitionChecks
{
    public static void Run(string scratch)
    {
        Check("[SubsPlease] Sousou no Frieren - 07 (1080p) [A1B2C3D4].mkv", "Sousou no Frieren", null, 7, AnimeKind.Episode, false);
        Check("[SubsPlease] Frieren - 07 (1080p).mkv", "Frieren", null, 7, AnimeKind.Episode, false);
        Check("[Erai-raws] Frieren - 07v2 [1080p][HEVC][AAC].mkv", "Frieren", null, 7, AnimeKind.Episode, false);
        Check("Show.Name.S02E03.1080p.WEB-DL.x265.mkv", "Show Name", 2, 3, AnimeKind.Episode, false);
        Check("Show 2x03.mkv", "Show", 2, 3, AnimeKind.Episode, false);
        Check("Episode 02.mkv", "Frieren", 1, 2, AnimeKind.Episode, false, "Frieren", "Season 1");
        Check("02.mkv", "Frieren", 2, 2, AnimeKind.Episode, false, "Frieren", "Staffel 2");
        Check("86 - 03.mkv", "86", null, 3, AnimeKind.Episode, false);
        Check("3-gatsu no Lion - 02.mkv", "3-gatsu no Lion", null, 2, AnimeKind.Episode, false);
        Check("[Group] 葬送のフリーレン - 07 [1080p].mkv", "葬送のフリーレン", null, 7, AnimeKind.Episode, false);
        Check("[Oshi no Ko] - 01.mkv", "Oshi no Ko", null, 1, AnimeKind.Episode, false);
        Check("Show 2nd Season - 03.mkv", "Show 2nd Season", 2, 3, AnimeKind.Episode, false);
        Check("Show Part 2 - 03.mkv", "Show Part 2", null, 3, AnimeKind.Episode, false);
        Check("Show - 07.5.mkv", "Show", null, null, AnimeKind.Unknown, true);
        Check("Show - 07,5.mkv", "Show", null, null, AnimeKind.Unknown, true);
        Check("Show - 01-12.mkv", "Show", null, null, AnimeKind.Unknown, true);
        Check("Show S01E01+02.mkv", "Show", 1, null, AnimeKind.Unknown, true);
        Check("Show S01E01 to 12.mkv", "Show", 1, null, AnimeKind.Unknown, true);
        Check("Show S01E01E02.mkv", "Show", 1, null, AnimeKind.Unknown, true);
        Check("Show S01E01-E02.mkv", "Show", 1, null, AnimeKind.Unknown, true);
        Check("Show S00E03.mkv", "Show", 0, null, AnimeKind.Special, true);
        Check("Episode 03.mkv", "Show", 0, null, AnimeKind.Special, true, "Show", "Season 0");
        Check("Show OVA - 01.mkv", "Show OVA", null, null, AnimeKind.Special, true);
        Check("Show SP01.mkv", "Show SP01", null, null, AnimeKind.Special, true);
        Check("Show Movie (2024).mkv", "Show Movie (2024)", null, null, AnimeKind.Movie, false);
        Check("Mob Psycho 100.mkv", "Mob Psycho 100", null, null, AnimeKind.Unknown, true);
        Check("86.mkv", "86", null, null, AnimeKind.Unknown, true);
        Check("2001 A Space Odyssey (2001).mkv", "2001 A Space Odyssey (2001)", null, null, AnimeKind.Unknown, true);
        Check("Show S02E03.mkv", "Show", 2, 3, AnimeKind.Episode, true, "Show", "Season 1");
        Check("Other Anime - 02.mkv", "Other Anime", null, 2, AnimeKind.Episode, true, "Frieren");

        string full = Path.Combine(scratch, "Frieren", "Season 1", "Episode 02.mkv");
        var fallback = AnimeRecognition.Detect(full);
        Require(fallback.Title == "Frieren" && fallback.Season == 1 && fallback.Episode == 2 && !fallback.NeedsReview,
            "A season directory should supply the show and season for a generic episode filename.");
        Require(AnimeRecognition.NormalizeTitle("Ｆｒｉｅｒｅｎ: Beyond Journey’s End") == "frieren beyond journey s end",
            "Comparison keys should normalize Unicode width, punctuation and case.");
        Require(AnimeRecognition.NormalizeTitle("Show Season 2") != AnimeRecognition.NormalizeTitle("Show Season 1"),
            "Season numbers must survive title normalization.");
        Require(AnimeRecognition.NormalizeTitle("86") == "86", "A numeric show title must survive normalization.");
        Require(AnimeRecognition.Detect(new string('a', 5000)).NeedsReview, "Oversized filename input should not be trusted.");
        Require(AnimeRecognition.Detect("Episode 02.mkv", "Loose videos").Title.Length == 0,
            "A generic loose-videos grouping must not become a catalogue search title.");
        var genericRoot = AnimeRecognition.Detect(Path.Combine(scratch, "private-person", "Videos", "Anime", "Episode 02.mkv"), "Loose videos");
        Require(genericRoot.Title.Length == 0 && genericRoot.NeedsReview,
            "Detection must not walk past a generic library root and mistake a private parent folder for an anime title.");
        Require(AnimeRecognition.Detect("Bad\ud800 - 02.mkv").NeedsReview,
            "An unusual filename with an unpaired surrogate should request review instead of crashing.");
    }

    private static void Check(string file, string title, int? season, int? episode, AnimeKind kind, bool review,
        string? collection = null, string? seasonName = null)
    {
        var actual = AnimeRecognition.Detect(file, collection, seasonName);
        var expected = new DetectedAnime(title, season, episode, kind, review);
        Require(actual == expected, $"Recognition of {file} expected {expected}; got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
