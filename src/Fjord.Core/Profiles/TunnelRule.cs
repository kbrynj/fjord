using System.Text.Json.Serialization;

namespace Fjord.Core.Profiles;

public enum TunnelKind
{
    /// <summary>ssh -L: a port on this PC leads to something the server can reach. The common one.</summary>
    Local,
    /// <summary>ssh -R: a port on the server leads back to something on this PC.</summary>
    Remote,
    /// <summary>ssh -D: SOCKS proxy — apps on this PC browse "from" the server's network.</summary>
    Dynamic,
}

/// <summary>What the "Open" button does for a running tunnel.</summary>
public enum TunnelOpenAction { None, Http, Https, RemoteDesktop }

/// <summary>One port forward. Field meaning depends on <see cref="Kind"/> — see <see cref="Describe"/>.</summary>
public sealed class TunnelRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public TunnelKind Kind { get; set; } = TunnelKind.Local;

    /// <summary>Local/Dynamic: port on this PC. Remote: port on the server.</summary>
    public int ListenPort { get; set; }
    /// <summary>Local: host as seen from the server ("localhost" = the server itself). Remote: host as seen from this PC.</summary>
    public string TargetHost { get; set; } = "localhost";
    public int TargetPort { get; set; }

    /// <summary>Start when the session connects.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>Listen on all network interfaces instead of only localhost (lets other machines use the tunnel).</summary>
    public bool AllowOthers { get; set; }
    public TunnelOpenAction OpenAction { get; set; } = TunnelOpenAction.None;
    /// <summary>Id of the preset it was made from (for the "how to use it" hint).</summary>
    public string? PresetId { get; set; }

    public TunnelRule Clone() => (TunnelRule)MemberwiseClone();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ShortForm : Name;

    /// <summary>Compact technical form, e.g. "localhost:15432 → db:5432".</summary>
    [JsonIgnore]
    public string ShortForm => Kind switch
    {
        TunnelKind.Local => $"localhost:{ListenPort} → {TargetHost}:{TargetPort}",
        TunnelKind.Remote => $"server:{ListenPort} → this PC {TargetHost}:{TargetPort}",
        _ => $"SOCKS proxy on localhost:{ListenPort}",
    };

    /// <summary>The address you use on this PC, e.g. "http://localhost:8080".</summary>
    [JsonIgnore]
    public string? OpenAddress => Kind == TunnelKind.Local ? OpenAction switch
    {
        TunnelOpenAction.Http => $"http://localhost:{ListenPort}",
        TunnelOpenAction.Https => $"https://localhost:{ListenPort}",
        _ => $"localhost:{ListenPort}",
    } : Kind == TunnelKind.Dynamic ? $"localhost:{ListenPort}" : null;

    /// <summary>Plain-language explanation shown in the UI.</summary>
    public string Describe(string serverName)
    {
        var target = IsLoopback(TargetHost) ? $"port {TargetPort} on {serverName} itself" : $"{TargetHost}:{TargetPort} (reached from {serverName})";
        return Kind switch
        {
            TunnelKind.Local =>
                $"Connecting to localhost:{ListenPort} on this PC takes you to {target}.",
            TunnelKind.Remote =>
                $"Programs on {serverName} that connect to localhost:{ListenPort} reach {(IsLoopback(TargetHost) ? "" : TargetHost + " via ")}port {TargetPort} on this PC.",
            _ =>
                $"Apps set to use the SOCKS5 proxy localhost:{ListenPort} browse as if they were on {serverName} — they can open anything {serverName} can reach.",
        };
    }

    /// <summary>One line telling the user what to do once it runs.</summary>
    public string UsageHint(string serverName) => Kind switch
    {
        TunnelKind.Dynamic => $"Firefox: Settings → Network → Manual proxy → SOCKS Host localhost, Port {ListenPort}, SOCKS v5, tick \"Proxy DNS\".",
        TunnelKind.Remote => $"On {serverName}, use localhost:{ListenPort}. Something must be listening on port {TargetPort} on this PC.",
        _ => OpenAction switch
        {
            TunnelOpenAction.Http or TunnelOpenAction.Https => $"Open {OpenAddress} in your browser.",
            TunnelOpenAction.RemoteDesktop => $"Remote Desktop to localhost:{ListenPort} (the Open button does it).",
            _ => PresetId switch
            {
                "sqlserver" => $"In SSMS / Azure Data Studio, server name: localhost,{ListenPort} (with a comma).",
                "postgres" or "mysql" => $"In your database tool (DBeaver, pgAdmin, HeidiSQL…): host localhost, port {ListenPort}.",
                _ => $"Point your program at localhost, port {ListenPort}.",
            },
        },
    };

    /// <summary>Returns a friendly problem description, or null if the rule is usable.</summary>
    public string? Validate()
    {
        if (ListenPort is < 1 or > 65535) return "Choose a port between 1 and 65535.";
        if (Kind != TunnelKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(TargetHost)) return "Fill in the target address.";
            if (TargetHost.Contains(' ') || TargetHost.Contains('/')) return "The target address should be just a host name or IP, like 192.168.1.20 — no http:// or spaces.";
            if (TargetPort is < 1 or > 65535) return "The target port must be between 1 and 65535.";
        }
        if (Kind == TunnelKind.Remote && ListenPort < 1024)
            return "Ports below 1024 on the server need root. Use 1024 or higher (e.g. 8080).";
        return null;
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1";
}
