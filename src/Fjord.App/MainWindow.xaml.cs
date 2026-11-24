using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Dialogs;
using Fjord.Features;
using Fjord.Services;
using Fjord.Views;

namespace Fjord;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        foreach (var feature in FeatureRegistry.GlobalFeatures)
        {
            var button = UiHelpers.ToolbarButton(feature.Glyph, feature.Title, feature.ToolTip,
                                                 (_, _) => feature.Execute(new FeatureContext(this)));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(10, 7, 10, 7);
            ToolsPanel.Children.Add(button);
        }

        AppServices.Profiles.Changed += (_, _) => Dispatcher.Invoke(RefreshProfiles);
        RefreshProfiles();
        UpdateWelcome();
    }

    // ───────────────────────── tabs (public API for features) ─────────────────────────

    public TerminalTab OpenTerminal(SessionProfile profile)
    {
        var tab = new TerminalTab(this, profile);
        tab.HotkeyPressed += (_, key) => OnTerminalHotkey(tab, key);
        AddTab(tab);
        return tab;
    }

    /// <summary>Opens an SFTP browser. Pass the terminal's endpoint to reuse its login.</summary>
    public SftpTab OpenSftp(SessionProfile profile, SshEndpoint? sharedEndpoint = null)
    {
        var tab = new SftpTab(this, profile, sharedEndpoint);
        AddTab(tab);
        return tab;
    }

    public ISessionTab? ActiveTab => (Tabs.SelectedItem as TabItem)?.Tag as ISessionTab;

    /// <summary>Adds any <see cref="ISessionTab"/> (must be a UIElement, e.g. a UserControl) as a closable tab.</summary>
    public void AddTab(ISessionTab tab)
    {
        var title = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = tab.Glyph, Style = (Style)FindResource("Glyph"), Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(title);

        var item = new TabItem { Header = header, Tag = tab, ToolTip = tab.Profile.Target };
        var close = new Button
        {
            Content = "\uE711",
            Style = (Style)FindResource("IconButton"),
            FontSize = 10,
            Padding = new Thickness(5),
            Margin = new Thickness(8, 0, -4, 0),
            ToolTip = "Close (Ctrl+Shift+W)",
            Focusable = false,
        };
        close.Click += (_, _) => CloseTab(item);
        header.Children.Add(close);
        header.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(item); };
        tab.TitleChanged += (_, _) => title.Text = tab.Title;

        ContentHost.Children.Add((UIElement)tab);
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        UpdateWelcome();
    }

    public void CloseTab(TabItem item)
    {
        if (item.Tag is ISessionTab tab)
        {
            tab.Close();
            ContentHost.Children.Remove((UIElement)tab);
        }
        Tabs.Items.Remove(item);
        UpdateWelcome();
    }

    private void OnTerminalHotkey(TerminalTab tab, string key)
    {
        var count = Tabs.Items.Count;
        switch (key)
        {
            case "nexttab" when count > 1: Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % count; break;
            case "prevtab" when count > 1: Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + count) % count; break;
            case "duplicate": OpenTerminal(tab.Profile); break;
            case "close":
                if (Tabs.Items.Cast<TabItem>().FirstOrDefault(t => t.Tag == tab) is { } item) CloseTab(item);
                break;
            case "snippets": SnippetRunner.ShowMenu(tab, tab.ButtonFor<SnippetsFeature>()); break;
            case { } k when k.StartsWith("snippet:") && int.TryParse(k[8..], out var n): SnippetRunner.RunNumber(tab, n); break;
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        var active = ActiveTab;
        foreach (UIElement child in ContentHost.Children)
            child.Visibility = ReferenceEquals(child, active) ? Visibility.Visible : Visibility.Collapsed;
        if (active is not null)
            Dispatcher.BeginInvoke(new Action(active.Activate), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateWelcome() =>
        WelcomePanel.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnClosing(CancelEventArgs e)
    {
        var open = Tabs.Items.Count;
        if (open > 0 && MessageBox.Show(this, $"Close {open} open session(s) and exit?", "Fjord",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        foreach (var item in Tabs.Items.Cast<TabItem>().ToList()) (item.Tag as ISessionTab)?.Close();
        base.OnClosing(e);
    }

    // ───────────────────────── sidebar: saved sessions ─────────────────────────

    private void RefreshProfiles()
    {
        var selectedId = (ProfileList.SelectedItem as SessionProfile)?.Id;
        var view = new ListCollectionView(AppServices.Profiles.All.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionProfile.GroupOrDefault)));
        view.Filter = MatchesSearch;
        ProfileList.ItemsSource = view;
        if (selectedId is { } id)
            ProfileList.SelectedItem = view.Cast<SessionProfile>().FirstOrDefault(p => p.Id == id);
    }

    private bool MatchesSearch(object o)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || o is not SessionProfile p) return true;
        return p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (p.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private SessionProfile? SelectedProfile => ProfileList.SelectedItem as SessionProfile;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        (ProfileList.ItemsSource as ICollectionView)?.Refresh();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ProfileList.Items.Count > 0)
        {
            ProfileList.SelectedIndex = 0;
            Connect_Click(sender, e);
        }
        else if (e.Key == Key.Down && ProfileList.Items.Count > 0)
        {
            ProfileList.SelectedIndex = 0;
            (ProfileList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        }
        else if (e.Key == Key.Escape) SearchBox.Clear();
    }

    private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ProfileList, d) is ListBoxItem)
            Connect_Click(sender, e);
    }

    private void ProfileList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Connect_Click(sender, e); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.F2: Edit_Click(sender, e); e.Handled = true; break;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } p) OpenTerminal(p);
    }

    private void OpenSftp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } p) OpenSftp(p);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e) => EditProfile(new SessionProfile(), isNew: true);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } p) EditProfile(p, isNew: false);
    }

    private void EditProfile(SessionProfile profile, bool isNew)
    {
        var dlg = new ProfileDialog(profile, isNew) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } result)
            AppServices.Profiles.Upsert(result);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } p) return;
        var copy = p.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = p.DisplayName + " (copy)";
        copy.LastConnected = null;
        EditProfile(copy, isNew: true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } p) return;
        if (MessageBox.Show(this, $"Delete the saved session '{p.DisplayName}'?", "Delete session",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            AppServices.Profiles.Delete(p.Id);
    }

    // ───────────────────────── quick connect & welcome ─────────────────────────

    private void QuickBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QuickPlaceholder.Visibility = QuickBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void QuickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) QuickConnect_Click(sender, e);
    }

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionProfile.TryParseQuick(QuickBox.Text, out var profile))
        {
            MessageBox.Show(this, "Use the form user@host, host or user@host:port.", "Quick connect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (profile.Username.Length == 0) profile.Username = AppServices.Settings.DefaultUsername;

        // Reuse a saved session for the same target, so its key/remembered password apply.
        var saved = AppServices.Profiles.All.FirstOrDefault(p =>
            p.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port &&
            (profile.Username.Length == 0 || p.Username == profile.Username));
        OpenTerminal(saved ?? profile);
        QuickBox.Clear();
    }

    private void WelcomeImport_Click(object sender, RoutedEventArgs e) => new ImportPuttyFeature().Execute(new FeatureContext(this));

    private void WelcomeKeygen_Click(object sender, RoutedEventArgs e) => new KeyGeneratorFeature().Execute(new FeatureContext(this));
}
