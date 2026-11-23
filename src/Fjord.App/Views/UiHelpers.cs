using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fjord.Views;

public static class UiHelpers
{
    /// <summary>Flat toolbar button with an icon and optional label, on top of the theme's Button style.</summary>
    public static Button ToolbarButton(string glyph, string? text, string? toolTip, RoutedEventHandler onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(text))
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            ToolTip = toolTip ?? text,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Focusable = false,
        };
        button.Click += onClick;
        return button;
    }

    public static void ShowError(DependencyObject? owner, string message, string title = "Fjord")
    {
        var window = owner is null ? null : Window.GetWindow(owner);
        if (window is not null)
            MessageBox.Show(window, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
