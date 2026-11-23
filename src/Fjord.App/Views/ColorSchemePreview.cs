using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Fjord.Services;

namespace Fjord.Views;

/// <summary>A small fake terminal showing what a colour scheme looks like (used in Settings and the session editor).</summary>
public sealed class ColorSchemePreview : Border
{
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        LineHeight = 17,
    };
    private readonly WrapPanel _swatches = new() { Margin = new Thickness(0, 8, 0, 0) };

    public ColorSchemePreview()
    {
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(10, 8, 10, 8);
        Child = new StackPanel { Children = { _text, _swatches } };
        Show(ColorSchemes.Find(null));
    }

    public void Show(ColorScheme s)
    {
        Brush B(string hex) => new SolidColorBrush(ColorScheme.ToColor(hex));
        Run R(string text, string hex, bool bold = false) => new(text) { Foreground = B(hex), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };

        Background = B(s.Background);
        _text.Foreground = B(s.Foreground);
        _text.Inlines.Clear();
        _text.Inlines.AddRange(new Inline[]
        {
            R("user@server", s.Ansi[10], bold: true), R(":", s.Foreground), R("~/app", s.Ansi[12], bold: true), R("$ ", s.Foreground),
            R("ls", s.Foreground), new LineBreak(),
            R("config/  ", s.Ansi[12], bold: true), R("deploy.sh  ", s.Ansi[10], bold: true), R("app.log  ", s.Foreground),
            R("backup.tar.gz", s.Ansi[9], bold: true), new LineBreak(),
            R("ERROR ", s.Ansi[1], bold: true), R("disk almost full  ", s.Foreground),
            R("WARN ", s.Ansi[3], bold: true), R("retrying  ", s.Foreground),
            R("OK", s.Ansi[2], bold: true), new LineBreak(),
            new Run("selected text") { Background = B(s.Selection), Foreground = B(s.Foreground) },
            R("  # comment", s.Ansi[8]), R("  link", s.Ansi[6]), R("  key", s.Ansi[5]),
        });

        _swatches.Children.Clear();
        foreach (var hex in s.Ansi)
            _swatches.Children.Add(new Border
            {
                Width = 16, Height = 10, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(2),
                Background = B(hex), BorderBrush = B(s.Foreground), BorderThickness = new Thickness(0.5),
            });
    }
}
