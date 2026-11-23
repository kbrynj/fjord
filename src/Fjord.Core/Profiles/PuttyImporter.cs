using Microsoft.Win32;

namespace Fjord.Core.Profiles;

/// <summary>Reads PuTTY's saved sessions from HKCU\Software\SimonTatham\PuTTY\Sessions.</summary>
public static class PuttyImporter
{
    private const string SessionsKey = @"Software\SimonTatham\PuTTY\Sessions";

    public static List<SessionProfile> ReadSessions()
    {
        var result = new List<SessionProfile>();
        if (!OperatingSystem.IsWindows()) return result;

        using var root = Registry.CurrentUser.OpenSubKey(SessionsKey);
        if (root is null) return result;

        foreach (var rawName in root.GetSubKeyNames())
        {
            using var s = root.OpenSubKey(rawName);
            if (s is null) continue;

            var protocol = s.GetValue("Protocol") as string ?? "ssh";
            var host = s.GetValue("HostName") as string ?? "";
            if (!protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(host))
                continue;   // skips "Default Settings", serial/telnet sessions

            var name = Uri.UnescapeDataString(rawName);   // PuTTY stores "My%20Server"
            var user = s.GetValue("UserName") as string ?? "";
            if (host.Contains('@'))                       // PuTTY allows user@host in HostName
            {
                var at = host.LastIndexOf('@');
                if (user.Length == 0) user = host[..at];
                host = host[(at + 1)..];
            }

            var keyFile = s.GetValue("PublicKeyFile") as string;   // despite the name: the .ppk private key
            var port = s.GetValue("PortNumber") is int p and > 0 ? p : 22;
            var keepAlive = s.GetValue("PingIntervalSecs") is int k ? k : 0;

            result.Add(new SessionProfile
            {
                Name = name,
                Group = "Imported from PuTTY",
                Host = host,
                Port = port,
                Username = user,
                Auth = string.IsNullOrWhiteSpace(keyFile) ? AuthMethod.Password : AuthMethod.PrivateKey,
                PrivateKeyPath = string.IsNullOrWhiteSpace(keyFile) ? null : keyFile,
                KeepAliveSeconds = keepAlive,
            });
        }
        return result;
    }
}
