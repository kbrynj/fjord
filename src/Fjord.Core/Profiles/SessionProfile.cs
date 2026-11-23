using System.Text.Json.Serialization;

namespace Fjord.Core.Profiles;

public enum AuthMethod
{
    /// <summary>Ask for a password when connecting (or use the remembered one).</summary>
    Password,
    /// <summary>Private key file (OpenSSH format, PEM or PuTTY .ppk), password as fallback.</summary>
    PrivateKey,
}

/// <summary>A saved connection. Add new settings here as plain properties — they persist automatically.</summary>
public sealed class SessionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Sidebar folder, e.g. a customer name. Empty = "Ungrouped".</summary>
    public string Group { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public AuthMethod Auth { get; set; } = AuthMethod.Password;
    public string? PrivateKeyPath { get; set; }
    /// <summary>DPAPI-encrypted (current Windows user only). Null = ask every time.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>0 = off. PuTTY calls this "Seconds between keepalives".</summary>
    public int KeepAliveSeconds { get; set; } = 30;
    /// <summary>Command run right after login (optional), e.g. "cd /opt/pos && bash".</summary>
    public string? StartupCommand { get; set; }
    /// <summary>Remote folder the SFTP browser opens in. Empty = home directory.</summary>
    public string? SftpStartPath { get; set; }
    public string? Notes { get; set; }
    /// <summary>Terminal colour scheme for this session (e.g. a red one for production). Null = the default from Settings.</summary>
    public string? ColorScheme { get; set; }
    /// <summary>Start logging terminal output to a file every time this session connects.</summary>
    public bool LogSessions { get; set; }
    public DateTime? LastConnected { get; set; }
    /// <summary>Port forwarding rules ("tunnels") for this session.</summary>
    public List<TunnelRule> Tunnels { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Target : Name;

    [JsonIgnore]
    public string Target => (string.IsNullOrEmpty(Username) ? "" : Username + "@") + Host + (Port == 22 ? "" : ":" + Port);

    [JsonIgnore]
    public string GroupOrDefault => string.IsNullOrWhiteSpace(Group) ? "Ungrouped" : Group;

    public SessionProfile Clone()
    {
        var copy = (SessionProfile)MemberwiseClone();
        copy.Tunnels = Tunnels.Select(t => t.Clone()).ToList();   // deep copy: dialogs edit the copy
        return copy;
    }

    /// <summary>Parses quick-connect input: "user@host", "host:2222", "user@host:2222".</summary>
    public static bool TryParseQuick(string input, out SessionProfile profile)
    {
        profile = new SessionProfile();
        input = input.Trim();
        if (input.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)) input = input[4..].Trim();
        if (input.Length == 0 || input.Contains(' ')) return false;

        var at = input.LastIndexOf('@');
        if (at > 0)
        {
            profile.Username = input[..at];
            input = input[(at + 1)..];
        }

        // [ipv6]:port, host:port, or bare host / bare ipv6
        if (input.StartsWith('['))
        {
            var close = input.IndexOf(']');
            if (close < 0) return false;
            profile.Host = input[1..close];
            var rest = input[(close + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) profile.Port = p6;
        }
        else if (input.Count(c => c == ':') == 1)
        {
            var parts = input.Split(':');
            profile.Host = parts[0];
            if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return false;
            profile.Port = port;
        }
        else
        {
            profile.Host = input;
        }

        profile.Name = profile.Target;
        return profile.Host.Length > 0;
    }
}
