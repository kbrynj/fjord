using Renci.SshNet;

namespace Fjord.Core.Ssh;

public enum PromptShell { Bash, Zsh, Ash, Unsupported }

/// <param name="RcFile">Where the block goes, e.g. "~/.bashrc".</param>
/// <param name="Problem">Why it can't be done (when <see cref="Kind"/> is Unsupported).</param>
public sealed record PromptShellInfo(PromptShell Kind, string ShellPath, string RcFile, string? Problem);

/// <param name="ReloadCommand">Typed into the open terminal to apply it right away (e.g. ". ~/.bashrc").</param>
public sealed record PromptResult(bool Success, string Message, string? ReloadCommand = null);

/// <summary>
/// Adds a coloured prompt (user@host:path) to the login user's shell start-up file on the server, inside a
/// clearly marked block that <see cref="Remove"/> takes out again. The colours are ANSI colours 1/2/4 (red,
/// green, blue), so they follow the terminal's colour scheme. Root gets red, so it stands out.
/// </summary>
public static class ColorPromptInstaller
{
    public const string BeginMarker = "# >>> Fjord colour prompt >>>";
    public const string EndMarker = "# <<< Fjord colour prompt <<<";
    private const string Note = "# Added by Fjord: colours the prompt using the terminal's colour scheme. Delete this block to undo.";

    /// <summary>Every file the block may have been added to.</summary>
    private const string CandidateFiles = "~/.bashrc ~/.zshrc ~/.profile ~/.bash_profile ~/.bash_login";

    // ───────────── the blocks ─────────────

    // \033 (octal) works in both bash and BusyBox ash prompts; \[ \] mark the escapes as zero-width.
    private const string PosixPs1 =
        "  if [ \"${EUID:-$(id -u)}\" = 0 ]; then __fjord_c='1;31'; else __fjord_c='1;32'; fi\n" +
        "  PS1=\"\\[\\033[${__fjord_c}m\\]\\u@\\h\\[\\033[0m\\]:\\[\\033[1;34m\\]\\w\\[\\033[0m\\]\\\\$ \"\n" +
        "  unset __fjord_c\n";

    public static string BlockFor(PromptShell kind) => kind switch
    {
        PromptShell.Bash => Wrap("if [ -n \"$BASH_VERSION\" ]; then\n" + PosixPs1 + "fi\n"),
        PromptShell.Ash => Wrap("if [ -z \"$BASH_VERSION\" ] && [ -z \"$ZSH_VERSION\" ]; then\n" + PosixPs1 + "fi\n"),
        PromptShell.Zsh => Wrap(
            "if [ -n \"$ZSH_VERSION\" ]; then\n" +
            "  if [ \"$EUID\" = 0 ]; then __fjord_c=red; else __fjord_c=green; fi\n" +
            "  PROMPT=\"%B%F{$__fjord_c}%n@%m%f%b:%B%F{blue}%~%f%b%# \"\n" +
            "  unset __fjord_c\n" +
            "fi\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Wrap(string body) => $"{BeginMarker}\n{Note}\n{body}{EndMarker}\n";

    // ───────────── server operations (blocking — call off the UI thread) ─────────────

    public static PromptShellInfo Detect(SshClient client)
    {
        var probe = Run(client, "uname -s && printf 'FJORD_SHELL=%s\\n' \"$SHELL\" && readlink -f /bin/sh");
        if (probe.ExitStatus != 0 || !probe.Result.Contains("FJORD_SHELL="))
            return Unsupported("", "This server doesn't look like Linux/Unix (Windows servers have no prompt to colour this way).");

        var lines = probe.Result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var shell = lines.FirstOrDefault(l => l.StartsWith("FJORD_SHELL="))?["FJORD_SHELL=".Length..] ?? "";
        var binSh = lines.LastOrDefault() ?? "";
        var name = shell.Split('/').Last();

        return name switch
        {
            "bash" => new(PromptShell.Bash, shell, "~/.bashrc", null),
            "zsh" => new(PromptShell.Zsh, shell, "~/.zshrc", null),
            "ash" => new(PromptShell.Ash, shell, "~/.profile", null),
            "sh" when binSh.Contains("busybox") => new(PromptShell.Ash, shell, "~/.profile", null),
            "sh" when binSh.EndsWith("bash") => new(PromptShell.Bash, shell, "~/.bashrc", null),
            "" => Unsupported(shell, "Couldn't tell which shell your account uses on this server."),
            _ => Unsupported(shell, $"Your account's shell here is {name}, which Fjord can't set a coloured prompt for. " +
                                    "Only bash, zsh and BusyBox sh are supported."),
        };

        static PromptShellInfo Unsupported(string path, string why) => new(PromptShell.Unsupported, path, "", why);
    }

    /// <summary>Files on the server that contain the Fjord block.</summary>
    public static IReadOnlyList<string> FindInstalled(SshClient client)
    {
        var cmd = Run(client, 
            $"for f in {CandidateFiles}; do [ -f \"$f\" ] && grep -qxF '{BeginMarker}' \"$f\" && echo \"FJORD_FILE=$f\"; done; true");
        return cmd.Result.Split('\n', StringSplitOptions.TrimEntries)
                  .Where(l => l.StartsWith("FJORD_FILE="))
                  .Select(l => l["FJORD_FILE=".Length..])
                  .ToList();
    }

    public static PromptResult Install(SshClient client, PromptShellInfo shell)
    {
        if (shell.Kind == PromptShell.Unsupported) return new(false, shell.Problem ?? "Not supported on this server.");

        RemoveFrom(client, CandidateFiles);   // re-installing replaces, never duplicates
        if (!Append(client, shell.RcFile, BlockFor(shell.Kind), out var error))
            return new(false, $"Couldn't write {shell.RcFile}: {error}");

        var reload = $". {shell.RcFile}";
        if (Verify(client, shell)) return new(true, $"Added a coloured prompt to {shell.RcFile}.", reload);

        // bash only reads ~/.bashrc for login shells if the login file sources it. If not, add the block there too.
        if (shell.Kind == PromptShell.Bash)
        {
            var login = Run(client, "for f in ~/.bash_profile ~/.bash_login ~/.profile; do [ -f \"$f\" ] && { echo \"$f\"; break; }; done").Result.Trim();
            if (login.Length == 0) login = "~/.bash_profile";
            if (Append(client, login, BlockFor(PromptShell.Bash), out error) && Verify(client, shell))
                return new(true, $"Added a coloured prompt to {shell.RcFile} and {login}.", reload);
        }

        return new(true, $"Added a coloured prompt to {shell.RcFile}, but couldn't confirm a new login picks it up " +
                         "(another start-up file may set its own prompt later). Open a new terminal to check.", reload);
    }

    public static PromptResult Remove(SshClient client)
    {
        var files = FindInstalled(client);
        if (files.Count == 0) return new(true, "There's no Fjord colour prompt on this server.");
        RemoveFrom(client, string.Join(' ', files.Select(f => $"'{f.Replace("'", "")}'")));
        var left = FindInstalled(client);
        return left.Count == 0
            ? new(true, $"Removed the coloured prompt from {string.Join(", ", files)}. New terminals get the server's own prompt again.")
            : new(false, $"Couldn't remove it from {string.Join(", ", left)} — edit the file and delete the Fjord block by hand.");
    }

    // ───────────── helpers ─────────────

    /// <summary>Runs a command with a time limit, so a start-up file that waits for input can't hang Fjord.</summary>
    private static SshCommand Run(SshClient client, string script)
    {
        var cmd = client.CreateCommand(script);
        cmd.CommandTimeout = TimeSpan.FromSeconds(20);
        try { cmd.Execute(); }
        catch (Renci.SshNet.Common.SshOperationTimeoutException) { /* treated as "no FJORD_ marker in the output" */ }
        return cmd;
    }

    private static bool Append(SshClient client, string file, string block, out string error)
    {
        // Quoted heredoc: the block is written exactly as is, no escaping or expansion.
        var script =
            $"f={file}; touch \"$f\" && {{ if [ -s \"$f\" ] && [ \"$(tail -c1 \"$f\")\" != \"\" ]; then echo >> \"$f\"; fi; " +
            $"cat >> \"$f\" <<'FJORD_EOF'\n{block}FJORD_EOF\n}} && echo FJORD_ADDED";
        var cmd = Run(client, script);
        error = (string.IsNullOrWhiteSpace(cmd.Error) ? cmd.Result : cmd.Error).Trim();
        return cmd.Result.Contains("FJORD_ADDED");
    }

    /// <summary>Deletes the block from each file, keeping the file itself (same owner and permissions).</summary>
    private static void RemoveFrom(SshClient client, string files) =>
        Run(client, 
            $"for f in {files}; do [ -f \"$f\" ] && grep -qxF '{BeginMarker}' \"$f\" || continue; " +
            $"awk -v b='{BeginMarker}' -v e='{EndMarker}' '$0==b{{s=1;next}} $0==e{{s=0;next}} !s' \"$f\" > \"$f.fjord-tmp\" && " +
            "cat \"$f.fjord-tmp\" > \"$f\"; rm -f \"$f.fjord-tmp\"; done; true");

    /// <summary>Starts an interactive login shell, like a new terminal would, and checks its prompt.</summary>
    private static bool Verify(SshClient client, PromptShellInfo shell)
    {
        var probe = shell.Kind switch
        {
            PromptShell.Zsh => $"{shell.ShellPath} -lic 'print -r -- \"FJORD_PS1=$PROMPT\"' </dev/null 2>/dev/null",
            _ => $"{shell.ShellPath} -lic 'printf \"FJORD_PS1=%s\\n\" \"$PS1\"' </dev/null 2>/dev/null",
        };
        var result = Run(client, probe).Result;
        var ps1 = result.Split('\n').FirstOrDefault(l => l.StartsWith("FJORD_PS1=")) ?? "";
        return shell.Kind == PromptShell.Zsh ? ps1.Contains("%F{blue}") : ps1.Contains("\\033[1;34m");
    }
}
