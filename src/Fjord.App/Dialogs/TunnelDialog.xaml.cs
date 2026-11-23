using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Services;
using Fjord.Views;

namespace Fjord.Dialogs;

/// <summary>
/// Create/edit a tunnel. Starts from a preset ("Database", "Remote Desktop"…) and explains, live,
/// in plain words and as a picture, what the settings will do.
/// </summary>
public partial class TunnelDialog : Window
{
    private readonly string _server;
    private readonly int? _originalPort;               // editing: the rule's own port isn't "in use by something else"
    private readonly HashSet<int> _otherLocalPorts;    // ports taken by the session's other tunnels
    private TunnelRule _rule;
    private bool _loading = true;

    public TunnelRule? Result { get; private set; }

    /// <param name="existing">Rule to edit, or null for a new one.</param>
    /// <param name="presetId">Preset to start from for a new rule.</param>
    public TunnelDialog(TunnelRule? existing, string serverName, string? presetId = null, IEnumerable<TunnelRule>? otherRules = null)
    {
        InitializeComponent();
        _server = string.IsNullOrWhiteSpace(serverName) ? "the server" : serverName;
        _otherLocalPorts = (otherRules ?? [])
            .Where(r => r.Id != existing?.Id && r.Kind != TunnelKind.Remote)
            .Select(r => r.ListenPort).ToHashSet();
        PresetList.ItemsSource = TunnelPresets.All;

        if (existing is not null)
        {
            Title = "Edit tunnel — " + existing.DisplayName;
            _rule = existing.Clone();
            _originalPort = existing.ListenPort;
            PresetList.SelectedItem = TunnelPresets.Find(existing.PresetId) ?? TunnelPresets.Find("custom");
            ShowPresetTexts(PresetList.SelectedItem as TunnelPreset);
            LoadFields();
        }
        else
        {
            Title = "New tunnel";
            _rule = new TunnelRule();
            var preset = TunnelPresets.Find(presetId) ?? TunnelPresets.All[0];
            PresetList.SelectedItem = preset;
            ApplyPreset(preset);
        }

        _loading = false;
        Refresh();
        Loaded += (_, _) => (existing is null ? PresetList : (Control)NameBox).Focus();
    }

    // ───────────────────────── presets ─────────────────────────

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetList.SelectedItem is not TunnelPreset preset) return;
        ApplyPreset(preset);
        Refresh();
    }

    private void ApplyPreset(TunnelPreset preset)
    {
        var keep = (_rule.Id, _rule.AutoStart);
        _rule = preset.CreateRule();
        (_rule.Id, _rule.AutoStart) = keep;

        if (_rule.Kind != TunnelKind.Remote && !PortIsUsable(_rule.ListenPort))
            _rule.ListenPort = FindFreePort(_rule.ListenPort + 1);

        ShowPresetTexts(preset);
        var was = _loading;
        _loading = true;
        LoadFields();
        _loading = was;
    }

    private void ShowPresetTexts(TunnelPreset? preset)
    {
        ExampleText.Text = preset?.Example ?? "";
        TargetHelp.Text = preset?.TargetHint ?? "";
    }

    // ───────────────────────── fields ⇄ rule ─────────────────────────

    private void LoadFields()
    {
        NameBox.Text = _rule.Name;
        ListenPortBox.Text = _rule.ListenPort.ToString();
        TargetHostBox.Text = _rule.TargetHost;
        TargetPortBox.Text = _rule.TargetPort > 0 ? _rule.TargetPort.ToString() : "";
        KindBox.SelectedIndex = (int)_rule.Kind;
        OpenBox.SelectedIndex = (int)_rule.OpenAction;
        AllowOthersBox.IsChecked = _rule.AllowOthers;
        AutoStartBox.IsChecked = _rule.AutoStart;
    }

    private void ReadFields()
    {
        _rule.Name = NameBox.Text.Trim();
        _rule.ListenPort = int.TryParse(ListenPortBox.Text, out var lp) ? lp : 0;
        _rule.TargetHost = TargetHostBox.Text.Trim();
        _rule.TargetPort = int.TryParse(TargetPortBox.Text, out var tp) ? tp : 0;
        _rule.Kind = KindBox.SelectedIndex >= 0 ? (TunnelKind)KindBox.SelectedIndex : TunnelKind.Local;
        _rule.OpenAction = OpenBox.SelectedIndex >= 0 ? (TunnelOpenAction)OpenBox.SelectedIndex : TunnelOpenAction.None;
        _rule.AllowOthers = AllowOthersBox.IsChecked == true;
        _rule.AutoStart = AutoStartBox.IsChecked == true;
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Refresh();
    }

    /// <summary>Updates labels, the picture, the explanation and validation from the current fields.</summary>
    private void Refresh()
    {
        ReadFields();
        var kind = _rule.Kind;
        bool localSide = kind != TunnelKind.Remote;

        ListenLabel.Text = kind switch
        {
            TunnelKind.Local => "PORT ON THIS PC",
            TunnelKind.Remote => $"PORT ON {_server.ToUpperInvariant()}",
            _ => "PROXY PORT ON THIS PC",
        };
        ListenHelp.Text = kind switch
        {
            TunnelKind.Local => "Any free number from 1024 to 65535. This is what you'll type on this PC (localhost:port).",
            TunnelKind.Remote => "Programs on the server will use localhost and this port.",
            _ => "Your browser's proxy setting will point here. 1080 is the usual choice.",
        };
        TargetPanel.Visibility = kind == TunnelKind.Dynamic ? Visibility.Collapsed : Visibility.Visible;
        TargetHostLabel.Text = kind == TunnelKind.Local ? $"ADDRESS — AS SEEN FROM {_server.ToUpperInvariant()}" : "ADDRESS ON THIS PC";
        TargetPortLabel.Text = "PORT";
        FreePortButton.Visibility = localSide ? Visibility.Visible : Visibility.Collapsed;
        OpenBox.IsEnabled = kind == TunnelKind.Local;

        UpdatePortStatus(localSide);
        BuildDiagram();

        var error = _rule.Validate();
        if (error is null && localSide && _otherLocalPorts.Contains(_rule.ListenPort))
            error = $"Another tunnel in this session already uses port {_rule.ListenPort} on this PC.";

        ExplainText.Text = error is null ? _rule.Describe(_server) : "";
        UsageText.Text = error is null ? _rule.UsageHint(_server) : "";
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error is null;
    }

    private void UpdatePortStatus(bool localSide)
    {
        if (!localSide || _rule.ListenPort is < 1 or > 65535 || _rule.ListenPort == _originalPort)
        {
            PortStatus.Text = "";
            return;
        }
        bool free = PortIsUsable(_rule.ListenPort);
        PortStatus.Text = free ? "✓ free" : "⚠ already in use on this PC";
        PortStatus.Foreground = free ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)) : new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    }

    private bool PortIsUsable(int port) => !_otherLocalPorts.Contains(port) && TunnelManager.IsLocalPortFree(port);

    private int FindFreePort(int from)
    {
        for (int p = Math.Max(1024, from); p < 65535; p++)
            if (PortIsUsable(p)) return p;
        return from;
    }

    private void FreePort_Click(object sender, RoutedEventArgs e)
    {
        ListenPortBox.Text = FindFreePort(_rule.ListenPort > 0 ? _rule.ListenPort + 1 : 10000).ToString();
    }

    // ───────────────────────── the picture ─────────────────────────

    private void BuildDiagram()
    {
        Diagram.Children.Clear();
        var r = _rule;
        string port(int p) => p > 0 ? p.ToString() : "?";

        switch (r.Kind)
        {
            case TunnelKind.Local:
                Diagram.Children.Add(Box("\uE7F8", "This PC", $"localhost:{port(r.ListenPort)}"));
                Diagram.Children.Add(Arrow("SSH (encrypted)"));
                if (TunnelRule.IsLoopback(r.TargetHost))
                    Diagram.Children.Add(Box("\uE968", _server, $"port {port(r.TargetPort)}"));
                else
                {
                    Diagram.Children.Add(Box("\uE968", _server, "passes it on"));
                    Diagram.Children.Add(Arrow("their network"));
                    Diagram.Children.Add(Box(r.OpenAction == TunnelOpenAction.RemoteDesktop ? "\uE7F4" : "\uE774",
                                             string.IsNullOrWhiteSpace(r.TargetHost) ? "?" : r.TargetHost, $"port {port(r.TargetPort)}"));
                }
                break;

            case TunnelKind.Remote:
                Diagram.Children.Add(Box("\uE968", _server, $"localhost:{port(r.ListenPort)}"));
                Diagram.Children.Add(Arrow("SSH (encrypted)"));
                Diagram.Children.Add(Box("\uE7F8", "This PC",
                    TunnelRule.IsLoopback(r.TargetHost) ? $"port {port(r.TargetPort)}" : $"{r.TargetHost}:{port(r.TargetPort)}"));
                break;

            default:
                Diagram.Children.Add(Box("\uE7F8", "This PC", $"proxy localhost:{port(r.ListenPort)}"));
                Diagram.Children.Add(Arrow("SSH (encrypted)"));
                Diagram.Children.Add(Box("\uE968", _server, "fetches for you"));
                Diagram.Children.Add(Arrow(""));
                Diagram.Children.Add(Box("\uE774", "Anything it can reach", "web pages, devices"));
                break;
        }
    }

    private static Border Box(string glyph, string title, string detail)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
        panel.Children.Add(new TextBlock { Text = detail, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.75 });
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            MinWidth = 110,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
        };
    }

    private static StackPanel Arrow(string label)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "⟶", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    // ───────────────────────── buttons ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadFields();
        if (_rule.Validate() is not null) return;
        if (string.IsNullOrWhiteSpace(_rule.Name)) _rule.Name = _rule.ShortForm;
        if (_rule.Kind == TunnelKind.Dynamic) { _rule.TargetHost = ""; _rule.TargetPort = 0; }
        if (_rule.AllowOthers && _rule.Kind != TunnelKind.Remote &&
            MessageBox.Show(this, "Other computers on your network will be able to use this tunnel to reach the server side. Continue?",
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Result = _rule;
        DialogResult = true;
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => GuideWindow.ShowSingle(this, GuideWindow.TunnelsPage);
}
