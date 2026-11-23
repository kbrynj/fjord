using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Fjord.Services;

namespace Fjord;

public partial class App : Application
{
    public App()
    {
        // WPF's built-in Windows 11 look (Mica, rounded controls, light/dark). Must be set before any window exists.
        ThemeMode = AppServices.Settings.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        BuildDerivedStyles();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    /// <summary>Styles that extend the Fluent theme's own styles are built here, after the theme is loaded.</summary>
    private void BuildDerivedStyles()
    {
        var iconButton = new Style(typeof(Button), TryFindResource(typeof(Button)) as Style);
        iconButton.Setters.Add(new Setter(Control.FontFamilyProperty, Resources["IconFont"]));
        iconButton.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        iconButton.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        iconButton.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        iconButton.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        iconButton.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        iconButton.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        Resources["IconButton"] = iconButton;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is { IsLoaded: true } owner)
            MessageBox.Show(owner, e.Exception.Message, "Fjord — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(e.Exception.Message, "Fjord — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
