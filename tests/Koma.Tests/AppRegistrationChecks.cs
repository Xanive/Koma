using System.Text;
using System.Text.Json;
using Koma;

internal static class AppRegistrationChecks
{
    public static void Run(string scratch)
    {
        const string client = "synthetic-app-id-123456";
        string valid = JsonSerializer.Serialize(new { Version = 1, ClientId = client, RedirectUri = MalOAuthFlow.RedirectUri });
        Require(MalAppRegistration.Parse(valid)?.ClientId == client, "A valid app registration should supply its public identifier.");
        Require(MalAppRegistration.Read(scratch) is null, "Missing registration must leave an unconfigured build usable.");
        string path = Path.Combine(scratch, MalAppRegistration.FileName);
        File.WriteAllText(path, valid);
        Require(MalAppRegistration.Read(scratch)?.ClientId == client, "An app registration should load from the application folder.");

        foreach (string bad in new[] {
            "", " ", "null", "[]", "{}", "{", valid + " {}",
            valid.Replace("\"Version\":1", "\"Version\":2"),
            valid.Replace("\"Version\":1", "\"Version\":\"1\""),
            valid.Replace("\"Version\":1", "\"Version\":null"),
            valid.Replace("\"Version\":1", "\"Version\":1.5"),
            valid.Replace("\"Version\":1", "\"version\":1"),
            valid.Replace("\"ClientId\"", "\"clientId\""),
            valid.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
            valid.Replace("\"ClientId\":", "\"ClientId\":\"other-synthetic-id\",\"ClientId\":"),
            valid.Replace(client, "short"), valid.Replace(client, new string('a', 257)),
            valid.Replace(client, "synthetic app id 123456"), valid.Replace(client, "synthetic-app-id-ä123456"),
            valid.Replace(client, "synthetic-app-id-\\u0000123456"),
            valid.Replace("43857", "43858"), valid.Replace("127.0.0.1", "localhost"),
            valid.Replace("callback", "callback/"), valid.Replace("http:", "https:"),
            valid.Replace("callback", "callback?next=https://example.test"),
            valid[..^1] + ",\"ClientSecret\":\"synthetic-never-ship\"}",
            valid[..^1] + ",\"AccessToken\":\"synthetic-never-ship\"}",
            valid[..^1] + ",\"RefreshToken\":\"synthetic-never-ship\"}",
            valid[..^1] + ",\"Endpoint\":\"https://example.test\"}",
            new string(' ', 4097) + valid,
            "[[[[[[]]]]]]"
        })
            Require(MalAppRegistration.Parse(bad) is null, "Malformed or credential-bearing registration must be ignored.");

        foreach (string id in new[] { new string('a', 16), new string('Z', 256), "synthetic.APP_id-123456" })
            Require(MalAppRegistration.Parse(valid.Replace(client, id))?.ClientId == id, "Native app identifiers should use the supported ASCII shape and bounds.");

        File.WriteAllBytes(path, [0xFF, 0xFE, 0xFF]);
        Require(MalAppRegistration.Read(scratch) is null, "Invalid UTF-8 must not break startup.");
        File.WriteAllText(path, new string('a', 4097));
        Require(MalAppRegistration.Read(scratch) is null, "Oversized app registration must not be loaded.");
        File.WriteAllText(path, valid[..^1] + ",\"ClientSecret\":\"synthetic\"}");
        Require(MalAppRegistration.Read(scratch) is null, "A file containing a client secret must never be accepted as public registration.");
        File.WriteAllText(path, valid, new UTF8Encoding(false));
        Require(MalAppRegistration.Read(scratch)?.ClientId == client, "A later valid registration should recover after malformed input.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
