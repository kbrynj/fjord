namespace Fjord.Core.Profiles;

/// <summary>A ready-made starting point in the tunnel dialog: "what do you want to reach?"</summary>
public sealed record TunnelPreset(
    string Id,
    string Title,
    string Subtitle,          // one line under the title in the picker
    string Glyph,             // Segoe Fluent Icons
    TunnelKind Kind,
    int ListenPort,
    string TargetHost,
    int TargetPort,
    TunnelOpenAction OpenAction,
    string Example,           // concrete example shown under the form
    string TargetHint)        // tooltip/placeholder for the target address
{
    public TunnelRule CreateRule() => new()
    {
        Name = Title,
        Kind = Kind,
        ListenPort = ListenPort,
        TargetHost = TargetHost,
        TargetPort = TargetPort,
        OpenAction = OpenAction,
        PresetId = Id,
    };
}

public static class TunnelPresets
{
    public static IReadOnlyList<TunnelPreset> All { get; } =
    [
        new("web-server", "Web page on the server", "An admin page that only works on the server itself", "\uE774",
            TunnelKind.Local, 8080, "localhost", 80, TunnelOpenAction.Http,
            "Portainer, a Docker container's web UI, Radarr/Sonarr, phpMyAdmin — anything the server only shows to itself. " +
            "Change the target port to the app's port (Portainer 9000, Radarr 7878, Sonarr 8989…).",
            "localhost means the server itself"),

        new("web-device", "Web page on a device in their network", "Router, printer, payment terminal, NAS…", "\uE968",
            TunnelKind.Local, 8081, "192.168.1.1", 80, TunnelOpenAction.Http,
            "You can SSH into one Linux machine at the customer, but the router's admin page is at 192.168.1.1. " +
            "Put the device's IP as the target — the server fetches the page for you.",
            "The device's IP address as seen from the server, e.g. 192.168.1.1"),

        new("postgres", "PostgreSQL database", "Use your own database tool against their database", "\uE8F1",
            TunnelKind.Local, 15432, "localhost", 5432, TunnelOpenAction.None,
            "The database only accepts connections from the server itself. With the tunnel running, connect DBeaver or pgAdmin " +
            "to localhost:15432 using the database username/password — no firewall changes needed.",
            "localhost if the database runs on the server, otherwise its IP"),

        new("mysql", "MySQL / MariaDB database", "Use HeidiSQL, DBeaver, Workbench…", "\uE8F1",
            TunnelKind.Local, 13306, "localhost", 3306, TunnelOpenAction.None,
            "Connect your MySQL tool to host localhost, port 13306. A different local port than 3306 avoids clashing with a MySQL running on your own PC.",
            "localhost if the database runs on the server, otherwise its IP"),

        new("sqlserver", "SQL Server database", "Reach a back-office SQL Server through a jump machine", "\uE8F1",
            TunnelKind.Local, 11433, "192.168.1.10", 1433, TunnelOpenAction.None,
            "In SSMS, type the server name as localhost,11433 (comma, not colon). Use the SQL login as usual. " +
            "Named instances need a fixed TCP port set in SQL Server Configuration Manager.",
            "IP of the SQL Server machine as seen from the server"),

        new("rdp", "Remote Desktop to a Windows PC", "A PC on their network that you can't reach directly", "\uE7F4",
            TunnelKind.Local, 13389, "192.168.1.50", 3389, TunnelOpenAction.RemoteDesktop,
            "The SSH server sits in the same network as the Windows PC. Fjord opens Remote Desktop (mstsc) to localhost:13389 for you.",
            "IP address of the Windows PC as seen from the server"),

        new("socks", "Browse through the server", "Your browser acts as if it were on their network", "\uE909",
            TunnelKind.Dynamic, 1080, "", 0, TunnelOpenAction.None,
            "Handy when there are many internal pages, or a site only allows the customer's IP address. " +
            "Set Firefox to use the SOCKS proxy (Fjord shows how) — your other browsers are unaffected.",
            ""),

        new("reverse", "Share something on my PC with the server", "Reverse tunnel: the server reaches a port on this PC", "\uE8EB",
            TunnelKind.Remote, 8080, "localhost", 3000, TunnelOpenAction.None,
            "You run a test API on this PC at port 3000. On the server, curl http://localhost:8080 reaches it — " +
            "useful for webhooks or letting a POS box call your dev build.",
            "Usually localhost (this PC)"),

        new("custom", "Something else", "Choose every setting yourself", "\uE713",
            TunnelKind.Local, 10000, "localhost", 80, TunnelOpenAction.None,
            "Any TCP service works: SSH to another machine (target port 22), VNC (5900), Redis (6379), MQTT (1883)… " +
            "UDP is not supported by SSH tunnels.",
            "Host name or IP as seen from the server"),
    ];

    public static TunnelPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}
