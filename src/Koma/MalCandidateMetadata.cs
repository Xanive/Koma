using System.Text.Json;

namespace Koma;

/// <summary>Catalogue and account-list nodes share the same bounded matching metadata.</summary>
internal static class MalCandidateMetadata
{
    public const string RequestedFields = "alternative_titles,main_picture,start_season,media_type,num_episodes,synopsis";

    public static MalAnimeCandidate? Read(JsonElement node, out bool incomplete)
    {
        incomplete = false;
        if (node.ValueKind != JsonValueKind.Object || !TryNumber(node, "id", out int id) || id <= 0 ||
            Text(node, "title", 300) is not { Length: > 0 } title)
        { incomplete = true; return null; }

        var aliases = new List<string>();
        string english = "";
        if (node.TryGetProperty("alternative_titles", out var names) && names.ValueKind != JsonValueKind.Null)
        {
            if (names.ValueKind != JsonValueKind.Object) incomplete = true;
            else
            {
                foreach (string key in new[] { "en", "ja" })
                {
                    if (!names.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                    string? name = value.ValueKind == JsonValueKind.String ? Clean(value.GetString(), 300) : null;
                    if (name is null) incomplete = true;
                    else if (name.Length > 0) { aliases.Add(name); if (key == "en") english = name; }
                }
                if (names.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind != JsonValueKind.Null)
                {
                    if (synonyms.ValueKind != JsonValueKind.Array) incomplete = true;
                    else
                    {
                        if (synonyms.GetArrayLength() > 32) incomplete = true;
                        foreach (var value in synonyms.EnumerateArray().Take(32))
                        {
                            string? synonym = value.ValueKind == JsonValueKind.String ? Clean(value.GetString(), 300) : null;
                            if (synonym is null) incomplete = true;
                            else if (synonym.Length > 0) aliases.Add(synonym);
                        }
                    }
                }
            }
        }

        int? year = null;
        if (node.TryGetProperty("start_season", out var season) && season.ValueKind != JsonValueKind.Null)
        {
            if (season.ValueKind == JsonValueKind.Object && TryNumber(season, "year", out int y) && y is >= 1900 and <= 2200) year = y;
            else incomplete = true;
        }
        int episodes = 0;
        if (node.TryGetProperty("num_episodes", out var count) && count.ValueKind != JsonValueKind.Null)
        {
            if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int number) && number is >= 0 and <= 100000) episodes = number;
            else incomplete = true;
        }
        string mediaType = Text(node, "media_type", 32) ?? "unknown";
        if (node.TryGetProperty("media_type", out var kind) && kind.ValueKind != JsonValueKind.Null &&
            (kind.ValueKind != JsonValueKind.String || Clean(kind.GetString(), 32) is null)) incomplete = true;

        string? picture = null;
        if (node.TryGetProperty("main_picture", out var images) && images.ValueKind == JsonValueKind.Object)
            picture = Picture(Text(images, "medium", 2048)) ?? Picture(Text(images, "large", 2048));
        return new(id, title, aliases.Distinct(StringComparer.Ordinal).ToArray(), year, mediaType, episodes)
        {
            EnglishTitle = english,
            PictureUrl = picture,
            Synopsis = Summary(node),
            SearchIncomplete = incomplete
        };
    }

    private static string? Picture(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri is { Scheme: "https", Port: 443, UserInfo.Length: 0, Fragment.Length: 0 } &&
        uri.Host is "cdn.myanimelist.net" or "images.myanimelist.net" &&
        uri.AbsolutePath.StartsWith("/images/anime/", StringComparison.Ordinal) ? uri.AbsoluteUri : null;

    private static string Summary(JsonElement node)
    {
        if (!node.TryGetProperty("synopsis", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 and <= 16000 } source || source.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) return "";
        string text = string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 600 ? text : text[..599].TrimEnd() + "…";
    }

    private static bool TryNumber(JsonElement node, string key, out int number)
    {
        number = 0;
        return node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    private static string? Text(JsonElement node, string key, int maximum) => node.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? Clean(value.GetString(), maximum) : null;
    private static string? Clean(string? text, int maximum) => text is null || text.Length > maximum || text.Any(char.IsControl) ? null : text.Trim();
}
