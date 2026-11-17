using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Koma;

internal static class ArtworkPolicy
{
    public static IEnumerable<string> Covers(string show, string? season)
    {
        foreach (string directory in new[] { show, season }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png" })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
    }

    public static string Key(string source)
    {
        var file = new FileInfo(source);
        string identity = $"v1|{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
