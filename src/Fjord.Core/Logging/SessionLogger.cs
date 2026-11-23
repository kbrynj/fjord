using System.Text;
using Fjord.Core.Profiles;

namespace Fjord.Core.Logging;

/// <summary>
/// Writes a terminal session's output to a file. Thread-safe: output arrives on the terminal's reader thread,
/// notes and Stop on the UI thread.
/// <list type="bullet">
/// <item>Plain text: colour codes and cursor control removed, \r-overwritten lines (progress bars) reduced to
/// their final state, full-screen programs (vim, top, less) replaced by a note. Optional time per line.</item>
/// <item>Raw: the exact bytes — replay with <c>cat file.log</c> in a terminal.</item>
/// </list>
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly FileStream _file;
    private readonly bool _raw;
    private readonly PlainTextConverter? _plain;
    private DateTime _lastFlush = DateTime.UtcNow;
    private bool _disposed;

    public string FilePath { get; }

    public SessionLogger(string filePath, bool raw, bool timestamps)
    {
        FilePath = filePath;
        _raw = raw;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // Append: reconnecting or restarting a log with the same name continues the file.
        _file = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (!raw) _plain = new PlainTextConverter(timestamps);
    }

    /// <summary>Terminal output, exactly as received from the server.</summary>
    public void WriteOutput(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_raw) _file.Write(data);
            else WriteText(_plain!.Feed(data));
            FlushIfDue();
        }
    }

    /// <summary>A line from Fjord itself, e.g. "Connected to …" — marked so it's not mistaken for server output.</summary>
    public void WriteNote(string text)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var line = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text} ===";
            if (_raw) _file.Write(Encoding.UTF8.GetBytes("\r\n" + line + "\r\n"));
            else WriteText(_plain!.Break() + line + Environment.NewLine);
            _file.Flush();
        }
    }

    private void WriteText(string text)
    {
        if (text.Length > 0) _file.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>At most once a second, so a crash loses little and fast output doesn't hammer the disk.</summary>
    private void FlushIfDue()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFlush).TotalSeconds < 1) return;
        _lastFlush = now;
        _file.Flush();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_plain is not null) WriteText(_plain.Break());
            _file.Dispose();
        }
    }

    // ───────────── file names ─────────────

    public const string DefaultFileNamePattern = "{session}_{date}_{time}.log";

    /// <summary>Placeholders: {session} {host} {user} {date} (yyyy-MM-dd) {time} (HHmmss). Unsafe characters become _.</summary>
    public static string BuildPath(string folder, string pattern, SessionProfile profile, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultFileNamePattern;
        var name = pattern
            .Replace("{session}", profile.DisplayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{host}", profile.Host, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", profile.Username, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Not Path.HasExtension: "…_10.0.0.5_2026-11-23" would count as having one.
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            name += ".log";
        return Path.Combine(Environment.ExpandEnvironmentVariables(folder), name);
    }
}

/// <summary>
/// Turns a stream of terminal output into readable text lines. A small state machine, so escape sequences
/// and UTF-8 characters split across reads are handled.
/// </summary>
public sealed class PlainTextConverter(bool timestamps)
{
    private enum State { Normal, Escape, Csi, String, StringEscape, SkipOne }

    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _csi = new();
    private State _state;
    private bool _pendingCr;          // \r seen: the next printable character starts the line over
    private bool _fullScreen;         // inside vim/top/less (alternate screen): don't log the drawing
    private char[] _chars = new char[4096];

    /// <summary>Feeds raw bytes, returns the complete lines they finished (with newlines).</summary>
    public string Feed(ReadOnlySpan<byte> data)
    {
        int needed = _utf8.GetCharCount(data, flush: false);
        if (_chars.Length < needed) _chars = new char[Math.Max(needed, _chars.Length * 2)];
        int n = _utf8.GetChars(data, _chars, flush: false);
        for (int i = 0; i < n; i++) Process(_chars[i]);
        return Take();
    }

    /// <summary>Ends the current partial line (before a note, or at the end of the log).</summary>
    public string Break()
    {
        if (_line.Length > 0) EndLine();
        _pendingCr = false;
        return Take();
    }

    private string Take()
    {
        var s = _out.ToString();
        _out.Clear();
        return s;
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case State.Normal:
                Normal(c);
                break;

            case State.Escape:
                _state = c switch
                {
                    '[' => State.Csi,
                    ']' or 'P' or 'X' or '^' or '_' => State.String,   // OSC (titles), DCS, SOS, PM, APC: skip to the end
                    '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' or ' ' => State.SkipOne,   // charset etc.: one more char
                    _ => State.Normal,                                  // two-character sequence (ESC 7, ESC =, …)
                };
                if (_state == State.Csi) _csi.Clear();
                break;

            case State.Csi:
                if (c >= '@' && c <= '~') { EndCsi(c); _state = State.Normal; }
                else if (_csi.Length < 64) _csi.Append(c);
                else _state = State.Normal;   // malformed — give up on it
                break;

            case State.String:
                if (c == '\a') _state = State.Normal;
                else if (c == '\x1b') _state = State.StringEscape;
                break;

            case State.StringEscape:   // ESC \ ends the string; anything else, keep skipping
                _state = c == '\\' ? State.Normal : State.String;
                break;

            case State.SkipOne:
                _state = State.Normal;
                break;
        }
    }

    private void Normal(char c)
    {
        switch (c)
        {
            case '\x1b': _state = State.Escape; return;
            case '\n':
                _pendingCr = false;
                if (!_fullScreen) EndLine();
                return;
            case '\r': _pendingCr = true; return;
            case '\b': if (_line.Length > 0) _line.Length--; return;
            case '\t': break;
            default:
                if (c < ' ' || c == '\x7f') return;   // bell and other controls
                break;
        }
        if (_fullScreen) return;
        if (_pendingCr) { _line.Clear(); _pendingCr = false; }   // "\r" redraw: progress bars, spinners
        _line.Append(c);
    }

    private void EndCsi(char final)
    {
        // Alternate screen on/off: ESC[?1049h / ESC[?1049l (also 47 and 1047).
        if ((final == 'h' || final == 'l') && _csi.Length > 1 && _csi[0] == '?')
        {
            var modes = _csi.ToString(1, _csi.Length - 1).Split(';');
            if (modes.Any(m => m is "1049" or "1047" or "47"))
            {
                bool on = final == 'h';
                if (on && !_fullScreen)
                {
                    if (_line.Length > 0) EndLine();
                    _out.Append(Stamp()).Append("[full-screen program — its screen is not logged]").Append(Environment.NewLine);
                }
                _fullScreen = on;
                _line.Clear();
                _pendingCr = false;
            }
        }
        // Everything else (colours, cursor movement, erase) has no meaning in a text log.
    }

    private void EndLine()
    {
        _out.Append(Stamp()).Append(_line.ToString().TrimEnd()).Append(Environment.NewLine);
        _line.Clear();
    }

    private string Stamp() => timestamps ? $"[{DateTime.Now:HH:mm:ss}] " : "";
}
