using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Koma;

/// <summary>Credentials belong to the Windows user, never to ordinary preferences or packages.</summary>
internal sealed class MalCredentialStore(string file)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Koma.MyAnimeList.Client.v1");

    public string? Read()
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 16384) return null;
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            try { return Valid(Encoding.UTF8.GetString(clear)) ? Encoding.UTF8.GetString(clear) : null; }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { return null; }
    }

    public void Save(string clientId)
    {
        if (!Valid(clientId)) throw new ArgumentException("Enter the Client ID issued by MyAnimeList.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        byte[] clear = Encoding.UTF8.GetBytes(clientId);
        string scratch = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(scratch, encrypted);
            File.Move(scratch, file, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }

    public void Clear() { if (File.Exists(file)) File.Delete(file); }

    private static bool Valid(string? value) => value is { Length: >= 16 and <= 256 } &&
        Regex.IsMatch(value, @"\A[a-zA-Z0-9._-]+\z", RegexOptions.CultureInvariant);
}
