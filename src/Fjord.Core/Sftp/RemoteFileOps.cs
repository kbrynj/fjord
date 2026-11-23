using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Fjord.Core.Sftp;

/// <summary>Unix permission bits (the "rwxr-xr-x" / 755 kind) and conversions for the permissions dialog.</summary>
public static class UnixMode
{
    public const int SetUid = 0x800, SetGid = 0x400, Sticky = 0x200;   // 4000, 2000, 1000 octal

    public static int FromAttributes(SftpFileAttributes a) =>
        (a.IsUIDBitSet ? SetUid : 0) | (a.IsGroupIDBitSet ? SetGid : 0) | (a.IsStickyBitSet ? Sticky : 0) |
        (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0) |
        (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0) |
        (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    /// <summary>"755", or "4755" when a special bit is set.</summary>
    public static string ToOctal(int mode)
    {
        var s = Convert.ToString(mode & 0xFFF, 8);
        return s.PadLeft((mode & 0xE00) != 0 ? 4 : 3, '0');
    }

    /// <summary>Accepts 3 or 4 octal digits ("644", "0755", "2775").</summary>
    public static bool TryParseOctal(string text, out int mode)
    {
        mode = 0;
        text = text.Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7')) return false;
        mode = Convert.ToInt32(text, 8);
        return true;
    }

    /// <summary>"rwxr-xr-x" (with s/t for the special bits, like ls).</summary>
    public static string ToSymbolic(int mode)
    {
        char Bit(int mask, char c) => (mode & mask) != 0 ? c : '-';
        char Special(int execMask, int specialMask, char set) =>
            (mode & specialMask) != 0 ? ((mode & execMask) != 0 ? set : char.ToUpperInvariant(set)) : Bit(execMask, 'x');
        return string.Concat(
            Bit(0x100, 'r'), Bit(0x80, 'w'), Special(0x40, SetUid, 's'),
            Bit(0x20, 'r'), Bit(0x10, 'w'), Special(0x8, SetGid, 's'),
            Bit(0x4, 'r'), Bit(0x2, 'w'), Special(0x1, Sticky, 't'));
    }

    /// <summary>
    /// Sets the full mode, special bits included. (SSH.NET's ChangePermissions takes the octal digits as a
    /// decimal number and clears setuid/setgid/sticky, so it isn't used.)
    /// </summary>
    public static void Apply(SftpClient sftp, string path, int mode)
    {
        var a = sftp.GetAttributes(path);
        a.IsUIDBitSet = (mode & SetUid) != 0;
        a.IsGroupIDBitSet = (mode & SetGid) != 0;
        a.IsStickyBitSet = (mode & Sticky) != 0;
        a.OwnerCanRead = (mode & 0x100) != 0;
        a.OwnerCanWrite = (mode & 0x80) != 0;
        a.OwnerCanExecute = (mode & 0x40) != 0;
        a.GroupCanRead = (mode & 0x20) != 0;
        a.GroupCanWrite = (mode & 0x10) != 0;
        a.GroupCanExecute = (mode & 0x8) != 0;
        a.OthersCanRead = (mode & 0x4) != 0;
        a.OthersCanWrite = (mode & 0x2) != 0;
        a.OthersCanExecute = (mode & 0x1) != 0;
        sftp.SetAttributes(path, a);
    }

    /// <summary>Folders need "x" to be opened: wherever read is allowed, allow entering too (chmod's +X).</summary>
    public static int ForFolder(int mode) =>
        mode | ((mode & 0x100) != 0 ? 0x40 : 0) | ((mode & 0x20) != 0 ? 0x8 : 0) | ((mode & 0x4) != 0 ? 0x1 : 0);
}

/// <summary>File-manager operations on the server that plain SFTP doesn't offer directly.</summary>
public static class RemoteFileOps
{
    public static string Combine(string dir, string name) => dir.TrimEnd('/') + "/" + name;

    public static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    public static string NameOf(string path) => path.TrimEnd('/').Split('/').Last();

    /// <summary>Resolves what the user typed in a rename/move box: a name, a relative path or an absolute path.</summary>
    public static string ResolveTarget(string currentDir, string input)
    {
        input = input.Trim();
        var path = input.StartsWith('/') ? input : Combine(currentDir, input);
        // Normalise "." and ".." so "../backup/x" works.
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        path = path.TrimEnd('/');
        folder = folder.TrimEnd('/');
        return path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    /// <summary>POSIX shell single-quoting: abc → 'abc', it's → 'it'\''s'.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>"report.txt" → "report (copy).txt", then "report (copy 2).txt"… — first name not taken in the folder.</summary>
    public static string UniqueCopyName(SftpClient sftp, string dir, string name)
    {
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (int i = 1; ; i++)
        {
            var candidate = stem + (i == 1 ? " (copy)" : $" (copy {i})") + ext;
            if (!sftp.Exists(Combine(dir, candidate))) return candidate;
        }
    }

    public static void DeleteRecursive(SftpClient client, string path, bool isDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isDir) { client.DeleteFile(path); return; }
        foreach (var f in client.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(client, f.FullName, f.IsDirectory, ct);   // symlinks to dirs report IsDirectory=false → unlinked, not followed
        client.DeleteDirectory(path);
    }

    /// <summary>
    /// Moves within the server. Uses the OpenSSH posix-rename extension when available, so an existing
    /// file at the destination is replaced instead of failing.
    /// </summary>
    public static void Move(SftpClient sftp, string source, string destination)
    {
        try { sftp.RenameFile(source, destination, isPosix: true); }
        catch (NotSupportedException) { sftp.RenameFile(source, destination); }
    }

    /// <summary>
    /// Copies a file or folder on the server. Runs <c>cp -a</c> over SSH when the server has a Unix shell
    /// (fast: the data never leaves the server). Otherwise (SFTP-only accounts, Windows servers) streams it
    /// through this PC over SFTP.
    /// </summary>
    /// <returns>true if done server-side, false if it was streamed.</returns>
    public static bool Copy(SshClient? ssh, SftpClient sftp, string source, string destination, bool isDir, CancellationToken ct,
                            Action<string>? progress = null)
    {
        if (ssh is { IsConnected: true })
        {
            int exit;
            string error;
            try
            {
                using var cmd = ssh.CreateCommand($"cp -a -- {ShellQuote(source)} {ShellQuote(destination)}");
                cmd.Execute();
                exit = cmd.ExitStatus is int e ? e : -1;
                error = (cmd.Error ?? "").Trim();
            }
            catch (SshException) when (ssh.IsConnected)
            {
                exit = 127;   // the server refused to run commands (SFTP-only account)
                error = "";
            }
            if (exit == 0) return true;
            // No cp here (Windows OpenSSH: "'cp' is not recognized…") → fall back to streaming. Real errors are reported.
            bool noCp = exit == 127 || error.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("not found", StringComparison.OrdinalIgnoreCase) && error.Contains("cp", StringComparison.Ordinal);
            if (!noCp) throw new IOException(error.Length > 0 ? error : $"cp failed (exit code {exit}).");
        }

        StreamCopy(sftp, source, destination, isDir, ct, progress);
        return false;
    }

    private static void StreamCopy(SftpClient sftp, string source, string destination, bool isDir, CancellationToken ct, Action<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var attrs = sftp.GetAttributes(source);
        if (isDir)
        {
            if (!sftp.Exists(destination)) sftp.CreateDirectory(destination);
            foreach (var f in sftp.ListDirectory(source).Where(f => f.Name is not "." and not ".."))
                StreamCopy(sftp, f.FullName, Combine(destination, f.Name), f.IsDirectory, ct, progress);
        }
        else
        {
            progress?.Invoke(NameOf(source));
            using var input = sftp.OpenRead(source);
            using var output = sftp.Open(destination, FileMode.Create, FileAccess.Write);
            input.CopyTo(output, 256 * 1024);
        }
        try { UnixMode.Apply(sftp, destination, UnixMode.FromAttributes(attrs)); } catch { /* best effort, like cp without -a */ }
    }

    /// <summary>
    /// Sets permissions. With <paramref name="recursive"/>, everything inside a folder gets the same mode,
    /// except that sub-folders keep "x" wherever "r" is allowed so they can still be opened.
    /// </summary>
    public static int ChangeMode(SftpClient sftp, string path, bool isDir, int mode, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int count = 1;
        UnixMode.Apply(sftp, path, isDir && recursive ? UnixMode.ForFolder(mode) : mode);
        if (isDir && recursive)
        {
            foreach (var f in sftp.ListDirectory(path).Where(f => f.Name is not "." and not ".." && !f.IsSymbolicLink))
                count += ChangeMode(sftp, f.FullName, f.IsDirectory, mode, recursive, ct);
        }
        return count;
    }
}
