using System.Net;
using System.Net.Sockets;
using Fjord.Core.Profiles;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Fjord.Core.Ssh;

/// <summary>Runs port forwards on one connected SshClient. Errors are turned into plain-language messages.</summary>
public sealed class TunnelManager(SshClient client, string serverName) : IDisposable
{
    private readonly Dictionary<Guid, ForwardedPort> _running = [];
    private readonly object _lock = new();

    /// <summary>A tunnel is up but a connection through it failed (target unreachable etc.). Raised on a background thread.</summary>
    public event Action<Guid, string>? ConnectionError;
    /// <summary>Something connected through the tunnel. Raised on a background thread.</summary>
    public event Action<Guid>? ConnectionOpened;

    public bool IsRunning(Guid ruleId)
    {
        lock (_lock) return _running.TryGetValue(ruleId, out var p) && p.IsStarted;
    }

    /// <summary>Starts the tunnel. Returns null on success, otherwise an explanation for the user.</summary>
    public string? Start(TunnelRule rule)
    {
        if (rule.Validate() is { } invalid) return invalid;
        if (IsRunning(rule.Id)) return null;
        if (!client.IsConnected) return "Not connected.";

        ForwardedPort? port = null;
        try
        {
            port = rule.Kind switch
            {
                TunnelKind.Local => new ForwardedPortLocal(LocalBind(rule), (uint)rule.ListenPort, rule.TargetHost, (uint)rule.TargetPort),
                TunnelKind.Dynamic => new ForwardedPortDynamic(LocalBind(rule), (uint)rule.ListenPort),
                _ => new ForwardedPortRemote(rule.AllowOthers ? IPAddress.Any : IPAddress.Loopback, (uint)rule.ListenPort,
                                             ResolveLocalTarget(rule.TargetHost), (uint)rule.TargetPort),
            };
            port.Exception += (_, e) => ConnectionError?.Invoke(rule.Id, ExplainConnectionError(rule, e.Exception));
            port.RequestReceived += (_, _) => ConnectionOpened?.Invoke(rule.Id);

            client.AddForwardedPort(port);
            port.Start();
        }
        catch (Exception ex)
        {
            if (port is not null)
            {
                try { client.RemoveForwardedPort(port); } catch { }
                port.Dispose();
            }
            return ExplainStartError(rule, ex);
        }

        lock (_lock) _running[rule.Id] = port;
        return null;
    }

    public void Stop(Guid ruleId)
    {
        ForwardedPort? port;
        lock (_lock)
        {
            if (!_running.Remove(ruleId, out port)) return;
        }
        try { port.Stop(); } catch { }
        try { client.RemoveForwardedPort(port); } catch { }
        port.Dispose();
    }

    public void StopAll()
    {
        List<Guid> ids;
        lock (_lock) ids = [.. _running.Keys];
        foreach (var id in ids) Stop(id);
    }

    public void Dispose() => StopAll();

    private static string LocalBind(TunnelRule rule) => rule.AllowOthers ? "0.0.0.0" : "127.0.0.1";

    /// <summary>"localhost" → 127.0.0.1 (not ::1 — most dev servers listen on IPv4 only).</summary>
    private static IPAddress ResolveLocalTarget(string host)
    {
        if (TunnelRule.IsLoopback(host)) return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = Dns.GetHostAddresses(host);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all[0];
    }

    private string ExplainStartError(TunnelRule rule, Exception ex)
    {
        if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied })
            return $"Port {rule.ListenPort} on this PC is already in use (another tunnel or program). Pick a different local port, e.g. {SuggestFreePort(rule.ListenPort + 1)}.";
        if (ex is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain })
            return $"Couldn't find the computer '{rule.TargetHost}'. Check the spelling, or use its IP address.";
        if (rule.Kind == TunnelKind.Remote && ex is SshException)
            return $"{serverName} refused to open port {rule.ListenPort}. It may already be in use there, or the server doesn't allow " +
                   "forwarding (AllowTcpForwarding / GatewayPorts in sshd_config).";
        return ex.Message;
    }

    private string ExplainConnectionError(TunnelRule rule, Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("prohibited", StringComparison.OrdinalIgnoreCase))
            return $"{serverName} doesn't allow port forwarding (AllowTcpForwarding no in sshd_config).";
        if (rule.Kind == TunnelKind.Local && (msg.Contains("refused", StringComparison.OrdinalIgnoreCase) || msg.Contains("Connect failed", StringComparison.OrdinalIgnoreCase)))
            return $"{serverName} couldn't reach {rule.TargetHost}:{rule.TargetPort}. Is the service running, and is the address right as seen from the server?";
        if (rule.Kind == TunnelKind.Remote && ex is SocketException)
            return $"Nothing answered on this PC at {rule.TargetHost}:{rule.TargetPort}. Start the program you want to share.";
        return msg;
    }

    // ───────────── helpers for picking ports ─────────────

    public static bool IsLocalPortFree(int port)
    {
        if (port is < 1 or > 65535) return false;
        if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
            return false;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static int SuggestFreePort(int preferred)
    {
        for (int p = Math.Clamp(preferred, 1024, 65000); p < 65535; p++)
            if (IsLocalPortFree(p)) return p;
        return preferred;
    }
}
