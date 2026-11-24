using Renci.SshNet;

namespace Fjord.Core.Sftp;

/// <summary>
/// Turns numeric user/group ids (what SFTP reports) into names, from the server's /etc/passwd and /etc/group.
/// Read over SFTP, so it works on SFTP-only accounts too. Ids that aren't in those files (LDAP users, files
/// copied from elsewhere) stay as numbers.
/// </summary>
public sealed class UserNames
{
    private readonly Dictionary<int, string> _users;
    private readonly Dictionary<int, string> _groups;

    public static UserNames Empty { get; } = new([], []);

    private UserNames(Dictionary<int, string> users, Dictionary<int, string> groups)
    {
        _users = users;
        _groups = groups;
    }

    public string User(int id) => _users.GetValueOrDefault(id) ?? id.ToString();
    public string Group(int id) => _groups.GetValueOrDefault(id) ?? id.ToString();
    public bool HasUser(int id) => _users.ContainsKey(id);
    public bool HasGroup(int id) => _groups.ContainsKey(id);

    /// <summary>Reads the server's files; returns <see cref="Empty"/> parts for whatever can't be read. Blocking.</summary>
    public static UserNames Load(SftpClient sftp) => new(TryParse(sftp, "/etc/passwd"), TryParse(sftp, "/etc/group"));

    private static Dictionary<int, string> TryParse(SftpClient sftp, string path)
    {
        try { return Parse(sftp.ReadAllText(path)); }
        catch { return []; }   // missing, not allowed (chroot), Windows server…
    }

    /// <summary>Parses passwd/group format: name:x:id:… — the first name wins if an id appears twice.</summary>
    public static Dictionary<int, string> Parse(string text)
    {
        var map = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(':');
            if (parts.Length >= 3 && parts[0].Length > 0 && int.TryParse(parts[2], out var id))
                map.TryAdd(id, parts[0]);
        }
        return map;
    }
}
