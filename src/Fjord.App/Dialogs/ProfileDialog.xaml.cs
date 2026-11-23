using System.IO;
using System.Windows;
using Fjord.Core.Keys;
using Fjord.Core.Profiles;
using Fjord.Services;
using Fjord.Views;
using Microsoft.Win32;

namespace Fjord.Dialogs;

/// <summary>Create / edit a saved session. Works on a copy; <see cref="Result"/> is set on Save.</summary>
public partial class ProfileDialog : Window
{
    private readonly SessionProfile _profile;
    private bool _forgetPassword;

    public SessionProfile? Result { get; private set; }

    public ProfileDialog(SessionProfile profile, bool isNew)
    {
        InitializeComponent();
        _profile = profile.Clone();
        Title = isNew ? "New session" : "Edit session — " + profile.DisplayName;

        NameBox.Text = _profile.Name;
        GroupCombo.ItemsSource = AppServices.Profiles.Groups.ToList();
        GroupCombo.Text = _profile.Group;
        HostBox.Text = _profile.Host;
        PortBox.Text = _profile.Port.ToString();
        UserBox.Text = string.IsNullOrEmpty(_profile.Username) && isNew ? AppServices.Settings.DefaultUsername : _profile.Username;
        KeyPathBox.Text = _profile.PrivateKeyPath ?? "";
        KeepAliveBox.Text = _profile.KeepAliveSeconds.ToString();
        StartupBox.Text = _profile.StartupCommand ?? "";
        SftpPathBox.Text = _profile.SftpStartPath ?? "";
        NotesBox.Text = _profile.Notes ?? "";

        // First entry = no override; it names the current default so it's clear what that means.
        var schemes = new List<string> { $"Use the default ({ColorSchemes.Find(AppServices.Settings.ColorScheme).Name})" };
        schemes.AddRange(ColorSchemes.All.Select(s => s.Name));
        SchemeBox.ItemsSource = schemes;
        SchemeBox.SelectedIndex = string.IsNullOrWhiteSpace(_profile.ColorScheme) ? 0 : Math.Max(0, schemes.IndexOf(ColorSchemes.Find(_profile.ColorScheme).Name));

        (_profile.Auth == AuthMethod.PrivateKey ? AuthKey : AuthPassword).IsChecked = true;
        SavedPasswordPanel.Visibility = _profile.ProtectedPassword is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateKeyPanel();
        RefreshTunnels();

        Loaded += (_, _) => (isNew ? HostBox : NameBox).Focus();
    }

    private void Auth_Changed(object sender, RoutedEventArgs e) => UpdateKeyPanel();

    /// <summary>Null when "Use the default" is selected.</summary>
    private string? ChosenScheme => SchemeBox.SelectedIndex > 0 ? SchemeBox.SelectedItem as string : null;

    private void SchemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SchemePreview?.Show(ColorSchemes.Find(ChosenScheme ?? AppServices.Settings.ColorScheme));

    private void UpdateKeyPanel()
    {
        if (KeyPanel is not null)
            KeyPanel.Visibility = AuthKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose private key",
            InitialDirectory = Directory.Exists(OpenSshKeyGenerator.DefaultSshDirectory) ? OpenSshKeyGenerator.DefaultSshDirectory : "",
            Filter = "Private keys|*.*|PuTTY keys (*.ppk)|*.ppk",
        };
        if (dlg.ShowDialog(this) == true)
        {
            if (dlg.FileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "That's the public key (.pub). Pick the file without .pub — the private key.", "Private key",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            KeyPathBox.Text = dlg.FileName;
        }
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        var win = new KeyGeneratorWindow { Owner = this };
        win.ShowDialog();
        if (win.SavedPrivateKeyPath is { } path)
        {
            KeyPathBox.Text = path;
            AuthKey.IsChecked = true;
        }
    }

    // ── tunnels ──

    private string ServerLabel => !string.IsNullOrWhiteSpace(NameBox.Text) ? NameBox.Text.Trim()
                                : !string.IsNullOrWhiteSpace(HostBox.Text) ? HostBox.Text.Trim() : "the server";

    private void RefreshTunnels()
    {
        TunnelList.ItemsSource = null;
        TunnelList.ItemsSource = _profile.Tunnels;
        TunnelsHeader.Text = _profile.Tunnels.Count == 0 ? "Tunnels (port forwarding)" : $"Tunnels (port forwarding) · {_profile.Tunnels.Count}";
    }

    private void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TunnelDialog(null, ServerLabel, otherRules: _profile.Tunnels) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } rule)
        {
            _profile.Tunnels.Add(rule);
            RefreshTunnels();
        }
    }

    private void EditTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelRule selected) return;
        var dlg = new TunnelDialog(selected, ServerLabel, otherRules: _profile.Tunnels) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } rule)
        {
            _profile.Tunnels[_profile.Tunnels.IndexOf(selected)] = rule;
            RefreshTunnels();
        }
    }

    private void RemoveTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is TunnelRule selected)
        {
            _profile.Tunnels.Remove(selected);
            RefreshTunnels();
        }
    }

    private void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        _forgetPassword = true;
        SavedPasswordPanel.Visibility = Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "Host name is required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number between 1 and 65535.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }
        if (AuthKey.IsChecked == true && string.IsNullOrWhiteSpace(KeyPathBox.Text))
        {
            MessageBox.Show(this, "Choose a private key file, or switch to password authentication.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _profile.Name = NameBox.Text.Trim();
        _profile.Group = GroupCombo.Text.Trim();
        _profile.Host = host;
        _profile.Port = port;
        _profile.Username = UserBox.Text.Trim();
        _profile.Auth = AuthKey.IsChecked == true ? AuthMethod.PrivateKey : AuthMethod.Password;
        _profile.PrivateKeyPath = string.IsNullOrWhiteSpace(KeyPathBox.Text) ? null : KeyPathBox.Text.Trim();
        _profile.KeepAliveSeconds = int.TryParse(KeepAliveBox.Text, out var ka) && ka >= 0 ? ka : 30;
        _profile.StartupCommand = NullIfEmpty(StartupBox.Text);
        _profile.SftpStartPath = NullIfEmpty(SftpPathBox.Text);
        _profile.Notes = NullIfEmpty(NotesBox.Text);
        _profile.ColorScheme = ChosenScheme;
        if (_forgetPassword) _profile.ProtectedPassword = null;
        if (string.IsNullOrWhiteSpace(_profile.Name)) _profile.Name = _profile.Target;

        Result = _profile;
        DialogResult = true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
