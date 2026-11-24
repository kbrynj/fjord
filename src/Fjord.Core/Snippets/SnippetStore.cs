using System.Text;
using System.Text.RegularExpressions;
using Fjord.Core.Storage;

namespace Fjord.Core.Snippets;

/// <summary>A saved command (one line) or macro (several lines) to send to a terminal.</summary>
public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Menu folder, e.g. "Docker" or "Logs". Empty = top level.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The text to send. Each line is sent as a separate command. May contain {ask:Label} placeholders.</summary>
    public string Command { get; set; } = "";
    /// <summary>true = press Enter after the (last) line, i.e. run it; false = just type it, so it can be edited first.</summary>
    public bool Run { get; set; } = true;

    public Snippet Clone() => (Snippet)MemberwiseClone();
}

/// <summary>Snippets in %APPDATA%\Fjord\snippets.json. Order in the file = order in menus (first nine get Ctrl+Shift+1…9).</summary>
public sealed class SnippetStore
{
    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<Snippet> Snippets { get; set; } = [];
    }

    private readonly string _path;
    private List<Snippet> _snippets;

    public SnippetStore(string? path = null)
    {
        _path = path ?? AppPaths.File("snippets.json");
        var model = File.Exists(_path) ? JsonStore.Load<FileModel>(_path) : new FileModel { Snippets = Examples() };
        _snippets = model.Snippets;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Snippet> All => _snippets;

    /// <summary>Replaces the whole list (the manager window edits a copy and saves it all at once).</summary>
    public void ReplaceAll(IEnumerable<Snippet> snippets)
    {
        _snippets = snippets.Select(s => s.Clone()).ToList();
        JsonStore.Save(_path, new FileModel { Snippets = _snippets });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A few useful ones so the menu isn't empty the first time (until the file is saved).</summary>
    private static List<Snippet> Examples() =>
    [
        new() { Folder = "System", Name = "Disk space", Command = "df -h" },
        new() { Folder = "System", Name = "Biggest folders here", Command = "du -xh --max-depth=1 . 2>/dev/null | sort -rh | head -20" },
        new() { Folder = "System", Name = "Memory and load", Command = "free -h; uptime" },
        new() { Folder = "Logs", Name = "Follow a service's log", Command = "journalctl -u {ask:Service name} -f" },
        new() { Folder = "Logs", Name = "Last 100 system log lines", Command = "journalctl -n 100 --no-pager" },
        new() { Folder = "Docker", Name = "Running containers", Command = "docker ps --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}'" },
    ];

    // ───────────── placeholders ─────────────

    private static readonly Regex AskPattern = new(@"\{ask:([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>The distinct {ask:Label} labels in a command, in order of appearance.</summary>
    public static IReadOnlyList<string> AskLabels(string command) =>
        AskPattern.Matches(command).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    /// <summary>Replaces {ask:Label} with the user's answers. Returns null if a question was cancelled.</summary>
    public static string? Expand(string command, Func<string, string?> ask)
    {
        var answers = new Dictionary<string, string>();
        foreach (var label in AskLabels(command))
        {
            var answer = ask(label);
            if (answer is null) return null;
            answers[label] = answer;
        }
        return AskPattern.Replace(command, m => answers[m.Groups[1].Value.Trim()]);
    }

    /// <summary>
    /// What to type into the terminal: lines separated by Enter (\r), and a final Enter when <paramref name="run"/>.
    /// Windows line endings and trailing blank lines are tidied up.
    /// </summary>
    public static string ToKeystrokes(string expanded, bool run)
    {
        var lines = expanded.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        var sb = new StringBuilder(string.Join('\r', lines));
        if (run) sb.Append('\r');
        return sb.ToString();
    }
}
