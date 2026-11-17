using System.Globalization;
using System.Text.Json;

namespace Koma;

internal static class UiText
{
    public static string Language { get; private set; } = "en";
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Catalogs = new(StringComparer.Ordinal);
    public static bool Supported(string? language) => language is "en" or "de" or "fr";

    public static IReadOnlyDictionary<string, string> Catalog(string language)
    {
        if (!Supported(language)) language = "en";
        if (Catalogs.TryGetValue(language, out var cached)) return cached;
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"Koma.Strings.{language}.json")
            ?? throw new InvalidOperationException("An interface language is missing from the application.");
        var result = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("An interface language could not be read.");
        Catalogs[language] = result;
        return result;
    }

    public static void Select(string language) => Language = Supported(language) ? language : "en";

    public static string Translate(string text, params object[] values)
    {
        string translated = Catalog(Language).GetValueOrDefault(text) ?? Catalog("en").GetValueOrDefault(text) ?? text;
        return values.Length == 0 ? translated : string.Format(CultureInfo.GetCultureInfo(Language), translated, values);
    }

    public static string Episodes(int count) => count == 1 ? Translate("1 episode") : Translate("{0} episodes", count);
    public static string Seasons(int count) => count == 1 ? Translate("1 season") : Translate("{0} seasons", count);
    public static string Collections(int count) => count == 1 ? Translate("1 collection") : Translate("{0} collections", count);
}
