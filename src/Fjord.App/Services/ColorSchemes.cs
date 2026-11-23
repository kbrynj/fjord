using System.Windows.Media;
using Fjord.Core.Profiles;

namespace Fjord.Services;

/// <summary>A terminal palette: background/foreground plus the 16 ANSI colours (8 normal, 8 bright).</summary>
public sealed record ColorScheme(string Name, string Background, string Foreground, string Cursor, string Selection, string[] Ansi)
{
    public bool IsLight => Luminance(Background) > 0.5;

    /// <summary>The object xterm.js expects as its <c>theme</c> option.</summary>
    public object ToXtermTheme() => new
    {
        background = Background, foreground = Foreground, cursor = Cursor, cursorAccent = Background,
        selectionBackground = Selection,
        black = Ansi[0], red = Ansi[1], green = Ansi[2], yellow = Ansi[3],
        blue = Ansi[4], magenta = Ansi[5], cyan = Ansi[6], white = Ansi[7],
        brightBlack = Ansi[8], brightRed = Ansi[9], brightGreen = Ansi[10], brightYellow = Ansi[11],
        brightBlue = Ansi[12], brightMagenta = Ansi[13], brightCyan = Ansi[14], brightWhite = Ansi[15],
    };

    public static Color ToColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static double Luminance(string hex)
    {
        var c = ToColor(hex);
        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
    }
}

/// <summary>Built-in colour schemes. Add one = add a line to <see cref="All"/>.</summary>
public static class ColorSchemes
{
    public const string DefaultName = "Campbell";

    public static IReadOnlyList<ColorScheme> All { get; } =
    [
        // Windows Terminal's default — what Fjord has always used.
        new("Campbell", "#0C0C0C", "#CCCCCC", "#FFFFFF", "#264F78",
            ["#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
             "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"]),
        new("One Half Dark", "#282C34", "#DCDFE4", "#A3B3CC", "#474E5D",
            ["#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
             "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"]),
        new("One Half Light", "#FAFAFA", "#383A42", "#4F525D", "#BFCEFF",
            ["#383A42", "#E45649", "#50A14F", "#C18401", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
             "#4F525E", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"]),
        new("Solarized Dark", "#002B36", "#839496", "#93A1A1", "#274642",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
             "#586E75", "#CB4B16", "#93A61A", "#C9A227", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"]),
        new("Solarized Light", "#FDF6E3", "#657B83", "#586E75", "#EEE8D5",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
             "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"]),
        new("Tango Dark", "#000000", "#D3D7CF", "#FFFFFF", "#3A5A80",
            ["#000000", "#CC0000", "#4E9A06", "#C4A000", "#3465A4", "#75507B", "#06989A", "#D3D7CF",
             "#555753", "#EF2929", "#8AE234", "#FCE94F", "#729FCF", "#AD7FA8", "#34E2E2", "#EEEEEC"]),
        new("Gruvbox Dark", "#282828", "#EBDBB2", "#EBDBB2", "#504945",
            ["#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984",
             "#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2"]),
        new("Nord", "#2E3440", "#D8DEE9", "#D8DEE9", "#434C5E",
            ["#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
             "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"]),
        new("Dracula", "#282A36", "#F8F8F2", "#F8F8F2", "#44475A",
            ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
             "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"]),
        // Fjord's own: tint the whole terminal so you can't mistake which kind of server you're on.
        new("Production (dark red)", "#2A0E0E", "#F2E4E4", "#FFFFFF", "#6B2A2A",
            ["#1A0808", "#FF6B6B", "#7CD17C", "#F2C94C", "#7AA7FF", "#D98BE0", "#6FD3D3", "#E8D8D8",
             "#9A7A7A", "#FF8F8F", "#A5E8A5", "#FFE08A", "#A3C2FF", "#EBB2F0", "#9EEAEA", "#FFFFFF"]),
        new("Test (dark green)", "#0E2112", "#E2F0E4", "#FFFFFF", "#2A5A33",
            ["#081509", "#FF6B6B", "#7CD17C", "#F2C94C", "#7AA7FF", "#D98BE0", "#6FD3D3", "#D8E8DA",
             "#7A9A7E", "#FF8F8F", "#A5E8A5", "#FFE08A", "#A3C2FF", "#EBB2F0", "#9EEAEA", "#FFFFFF"]),
    ];

    /// <summary>Unknown or empty names fall back to the default, so a renamed scheme never breaks a session.</summary>
    public static ColorScheme Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>The session's own scheme if it has one, otherwise the default from Settings.</summary>
    public static ColorScheme For(SessionProfile profile) =>
        Find(string.IsNullOrWhiteSpace(profile.ColorScheme) ? AppServices.Settings.ColorScheme : profile.ColorScheme);
}
