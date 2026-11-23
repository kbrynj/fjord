using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Fjord.Core.Storage;
using Fjord.Services;

namespace Fjord.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _s = AppServices.Settings;

    public SettingsDialog()
    {
        InitializeComponent();
        FontBox.Text = _s.FontFamily;
        FontSizeBox.Text = _s.FontSize.ToString();
        ScrollbackBox.Text = _s.Scrollback.ToString();
        CopyOnSelectBox.IsChecked = _s.CopyOnSelect;
        CursorBlinkBox.IsChecked = _s.CursorBlink;
        SchemeBox.ItemsSource = ColorSchemes.All.Select(s => s.Name).ToList();
        SchemeBox.SelectedItem = ColorSchemes.Find(_s.ColorScheme).Name;
        TwoPaneBox.IsChecked = _s.SftpTwoPane;
        DefaultUserBox.Text = _s.DefaultUsername;
        ThemeBox.SelectedIndex = _s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        DataFolderRun.Text = AppPaths.DataDirectory;
    }

    private void SchemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SchemePreview?.Show(ColorSchemes.Find(SchemeBox.SelectedItem as string));

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? new AppSettings().FontFamily : FontBox.Text.Trim();
        _s.FontSize = int.TryParse(FontSizeBox.Text, out var fs) ? Math.Clamp(fs, 6, 40) : 14;
        _s.Scrollback = int.TryParse(ScrollbackBox.Text, out var sb) ? Math.Clamp(sb, 100, 200_000) : 10000;
        _s.CopyOnSelect = CopyOnSelectBox.IsChecked == true;
        _s.CursorBlink = CursorBlinkBox.IsChecked == true;
        _s.ColorScheme = SchemeBox.SelectedItem as string ?? ColorSchemes.DefaultName;
        _s.SftpTwoPane = TwoPaneBox.IsChecked == true;
        _s.DefaultUsername = DefaultUserBox.Text.Trim();
        _s.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "System";
        _s.Save();
        DialogResult = true;
    }
}
