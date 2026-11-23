using Renci.SshNet;

namespace Fjord.Core.Ssh;

public sealed record InstallResult(bool Success, bool AlreadyPresent, string Message);

/// <summary>Fjord's version of `ssh-copy-id`: appends a public key to ~/.ssh/authorized_keys.</summary>
public static class AuthorizedKeysInstaller
{
    public static InstallResult Install(SshClient client, string publicKeyLine)
    {
        var key = publicKeyLine.Trim();
        var parts = key.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("ssh-") && !parts[0].StartsWith("ecdsa-") && !parts[0].StartsWith("sk-"))
            return new InstallResult(false, false, "That doesn't look like an OpenSSH public key (expected 'ssh-ed25519 AAAA… comment').");
        if (key.Contains('\'') || key.Contains('\n'))
            return new InstallResult(false, false, "The public key contains characters that can't be installed safely.");

        // Detect Windows OpenSSH servers (cmd/PowerShell as default shell) — different file location and rules.
        var probe = client.RunCommand("uname -s");
        if (probe.ExitStatus != 0)
            return new InstallResult(false, false,
                "The server doesn't look like Linux/Unix (uname failed). For Windows OpenSSH servers, see the key guide: " +
                "admins use C:\\ProgramData\\ssh\\administrators_authorized_keys.");

        // Match on the base64 part only, so a different comment doesn't create a duplicate.
        var blob = parts[1];
        var script =
            "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
            "chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys && " +
            $"if grep -qF '{blob}' ~/.ssh/authorized_keys; then echo FJORD_PRESENT; " +
            // make sure the file ends with a newline before appending
            $"else [ -s ~/.ssh/authorized_keys ] && [ \"$(tail -c1 ~/.ssh/authorized_keys)\" != \"\" ] && echo >> ~/.ssh/authorized_keys; " +
            $"echo '{key}' >> ~/.ssh/authorized_keys && echo FJORD_ADDED; fi";

        var cmd = client.RunCommand(script);
        if (cmd.Result.Contains("FJORD_PRESENT"))
            return new InstallResult(true, true, "The key was already in ~/.ssh/authorized_keys.");
        if (cmd.Result.Contains("FJORD_ADDED"))
            return new InstallResult(true, false, "Key added to ~/.ssh/authorized_keys.");
        return new InstallResult(false, false, "Installing the key failed: " + (string.IsNullOrWhiteSpace(cmd.Error) ? cmd.Result : cmd.Error).Trim());
    }
}
