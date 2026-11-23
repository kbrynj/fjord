using System.IO;
using System.Windows;
using Fjord.Core.Keys;
using Microsoft.Win32;

namespace Fjord.Views;

public partial class KeyGeneratorWindow : Window
{
    private bool _pathEdited;

    /// <summary>Set after a successful save (used by the session dialog's "Generate one" link).</summary>
    public string? SavedPrivateKeyPath { get; private set; }

    public KeyGeneratorWindow()
    {
        InitializeComponent();
        CommentBox.Text = $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();
        SuggestPath();
        PathBox.TextChanged += (_, _) => { _pathEdited = PathBox.IsKeyboardFocusWithin || _pathEdited; UpdatePathHint(); };
    }

    private KeyAlgorithm Algorithm => TypeRsa.IsChecked == true ? KeyAlgorithm.Rsa4096 : KeyAlgorithm.Ed25519;

    private void Type_Changed(object sender, RoutedEventArgs e)
    {
        if (PathBox is not null && !_pathEdited) SuggestPath();
    }

    /// <summary>Default ~/.ssh/id_ed25519, or id_ed25519_2, _3… if that already exists.</summary>
    private void SuggestPath()
    {
        var dir = OpenSshKeyGenerator.DefaultSshDirectory;
        var baseName = OpenSshKeyGenerator.DefaultFileName(Algorithm);
        var path = Path.Combine(dir, baseName);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{baseName}_{i}");
        PathBox.Text = path;
        UpdatePathHint();
    }

    private void UpdatePathHint()
    {
        if (PathHint is null) return;
        PathHint.Text = File.Exists(PathBox.Text)
            ? "⚠ A key with this name exists and will be replaced — servers using the old key will stop accepting it."
            : $"Creates {Path.GetFileName(PathBox.Text)} (private) and {Path.GetFileName(PathBox.Text)}.pub (public).";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(OpenSshKeyGenerator.DefaultSshDirectory);
        var dlg = new SaveFileDialog
        {
            Title = "Save private key as",
            InitialDirectory = OpenSshKeyGenerator.DefaultSshDirectory,
            FileName = Path.GetFileName(PathBox.Text),
            Filter = "Private key (no extension)|*.*",
            AddExtension = false,
            OverwritePrompt = false,
        };
        if (dlg.ShowDialog(this) == true)
        {
            _pathEdited = true;
            PathBox.Text = dlg.FileName;
        }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (PassBox.Password != PassConfirmBox.Password)
        {
            MessageBox.Show(this, "The passphrases don't match.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = PathBox.Text.Trim();
        if (path.Length == 0) return;
        if (File.Exists(path) && MessageBox.Show(this, $"{path} already exists.\n\nReplace it? The old key will be lost.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (PassBox.Password.Length == 0 && MessageBox.Show(this,
                "Save the key without a passphrase?\n\nAnyone who gets a copy of the file can log in as you. " +
                "That's common for keys only used on this PC, but a passphrase is safer.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        GenerateButton.IsEnabled = false;
        GenerateButton.Content = "Generating…";
        try
        {
            var alg = Algorithm;
            var comment = CommentBox.Text;
            var pass = PassBox.Password;
            var key = await Task.Run(() => OpenSshKeyGenerator.Generate(alg, comment, pass));
            OpenSshKeyGenerator.Save(key, path, overwrite: true);

            SavedPrivateKeyPath = path;
            PublicKeyBox.Text = key.PublicKeyLine;
            FingerprintText.Text = key.Fingerprint;
            SavedText.Text = $"Saved {path} (private{(key.IsEncrypted ? ", passphrase protected" : "")}) and {path}.pub.";
            ResultPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create the key:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            GenerateButton.Content = "Generate and save";
            PassBox.Clear();
            PassConfirmBox.Clear();
        }
    }

    private void CopyPublic_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(PublicKeyBox.Text); } catch { }
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => GuideWindow.ShowSingle(this);
}
