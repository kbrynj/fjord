using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Fjord.Core.Sftp;

namespace Fjord.Dialogs;

/// <summary>chmod with checkboxes. The checkboxes and the octal box stay in sync.</summary>
public partial class PermissionsDialog : Window
{
    private readonly (CheckBox Box, int Mask)[] _bits;
    private int _mode;
    private bool _syncing;

    /// <summary>The chosen mode (including any setuid/setgid/sticky bits it started with).</summary>
    public int Mode => _mode;
    public bool Recursive => RecursiveCheck.IsChecked == true;

    public PermissionsDialog(string target, string owner, int initialMode, bool anyFolder, bool onlyFolders)
    {
        InitializeComponent();
        _bits = [(UR, 0x100), (UW, 0x80), (UX, 0x40), (GR, 0x20), (GW, 0x10), (GX, 0x8), (OR, 0x4), (OW, 0x2), (OX, 0x1)];
        TargetText.Text = target;
        OwnerText.Text = owner;
        if (onlyFolders) ExecuteHeader.Text = "Open";   // x on a folder = may enter it
        if (anyFolder) RecursiveCheck.Visibility = RecursiveHint.Visibility = Visibility.Visible;
        SetMode(initialMode, updateText: true);
        Loaded += (_, _) => { OctalBox.Focus(); OctalBox.SelectAll(); };
    }

    private void SetMode(int mode, bool updateText)
    {
        _mode = mode;
        _syncing = true;
        foreach (var (box, mask) in _bits) box.IsChecked = (mode & mask) != 0;
        if (updateText) OctalBox.Text = UnixMode.ToOctal(mode);
        SymbolicText.Text = UnixMode.ToSymbolic(mode);
        _syncing = false;
    }

    private void Bit_Click(object sender, RoutedEventArgs e)
    {
        var mode = _mode & 0xE00;   // keep special bits
        foreach (var (box, mask) in _bits) if (box.IsChecked == true) mode |= mask;
        SetMode(mode, updateText: true);
    }

    private void OctalBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (UnixMode.TryParseOctal(OctalBox.Text, out var mode))
        {
            // Three digits only change rwx; the special bits stay as they were.
            if (OctalBox.Text.Trim().Length == 3) mode |= _mode & 0xE00;
            SetMode(mode, updateText: false);
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string octal } && UnixMode.TryParseOctal(octal, out var mode))
            SetMode(mode | (_mode & 0xE00), updateText: true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!UnixMode.TryParseOctal(OctalBox.Text, out _))
        {
            MessageBox.Show(this, "Use 3 or 4 digits from 0 to 7, for example 644 or 755.", "Permissions",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
