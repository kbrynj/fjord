using Fjord.Core.Storage;

namespace Fjord.Core.Ssh;

public enum HostKeyStatus { Unknown, Trusted, Changed }

public sealed record KnownHost(string KeyType, string Fingerprint, DateTime FirstSeen);

/// <summary>
/// Trust-on-first-use host key store (%APPDATA%\Fjord\known_hosts.json), keyed by "host:port".
/// A changed key is reported as <see cref="HostKeyStatus.Changed"/> — possible man-in-the-middle.
/// </summary>
public sealed class KnownHostsStore
{
    private readonly string _path;
    private readonly Dictionary<string, KnownHost> _hosts;
    private readonly object _lock = new();

    public KnownHostsStore(string? path = null)
    {
        _path = path ?? AppPaths.File("known_hosts.json");
        _hosts = new Dictionary<string, KnownHost>(JsonStore.Load<Dictionary<string, KnownHost>>(_path), StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    public HostKeyStatus Check(string host, int port, string keyType, string fingerprint, out KnownHost? existing)
    {
        lock (_lock)
        {
            if (!_hosts.TryGetValue(Key(host, port), out existing)) return HostKeyStatus.Unknown;
            return existing.Fingerprint == fingerprint ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
        }
    }

    public void Trust(string host, int port, string keyType, string fingerprint)
    {
        lock (_lock)
        {
            _hosts[Key(host, port)] = new KnownHost(keyType, fingerprint, DateTime.Now);
            JsonStore.Save(_path, _hosts);
        }
    }
}
