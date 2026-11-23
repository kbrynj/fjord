using System.Diagnostics;
using System.IO;
using System.Windows;
using Fjord.Core.Keys;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Dialogs;
using Fjord.Services;
using Fjord.Views;
using Microsoft.Win32;

namespace Fjord.Features;

// ───────────────────────── session toolbar ─────────────────────────

/// <summary>Opens an SFTP browser for the same server, reusing the terminal's login.</summary>
public sealed class OpenSftpFeature : IAppFeature
{
    public string Title => "SFTP";
    public string Glyph => "\uE8B7";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Browse files on this server (same login, no new password prompt)";

    public void Execute(FeatureContext ctx)
    {
        if (ctx.Terminal is null) return;
        ctx.Shell.OpenSftp(ctx.Terminal.Profile, ctx.Terminal.Endpoint);
    }
}

/// <summary>Shows/hides the tunnels (port forwarding) panel beside the terminal.</summary>
public sealed class TunnelsFeature : IAppFeature
{
    public string Title => "Tunnels";
    public string Glyph => "\uE8EB";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Port forwarding: reach databases, admin pages and PCs behind this server from your own PC";

    public void Execute(FeatureContext ctx) => ctx.Terminal?.ToggleTunnels();
}

/// <summary>ssh-copy-id: adds a public key to ~/.ssh/authorized_keys on the connected server.</summary>
public sealed class InstallKeyFeature : IAppFeature
{
    public string Title => "Install key";
    public string Glyph => "\uE8D7";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Add your public key to this server so you can log in without a password";

    public async void Execute(FeatureContext ctx)
    {
        var tab = ctx.Terminal;
        if (tab?.Client is not { } client)
        {
            MessageBox.Show(ctx.Shell, "Connect first, then install the key.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sshDir = OpenSshKeyGenerator.DefaultSshDirectory;
        var dlg = new OpenFileDialog
        {
            Title = "Choose the PUBLIC key (.pub) to install",
            Filter = "Public keys (*.pub)|*.pub|All files|*.*",
            InitialDirectory = Directory.Exists(sshDir) ? sshDir : "",
        };
        if (dlg.ShowDialog(ctx.Shell) != true) return;

        string line;
        try { line = File.ReadAllText(dlg.FileName).Trim(); }
        catch (Exception ex) { UiHelpers.ShowError(ctx.Shell, ex.Message, Title); return; }

        if (line.Contains("PRIVATE KEY"))
        {
            UiHelpers.ShowError(ctx.Shell, "That's a PRIVATE key — never copy it to a server. Choose the .pub file.", Title);
            return;
        }
        if (line.StartsWith("---- BEGIN SSH2 PUBLIC KEY"))
        {
            UiHelpers.ShowError(ctx.Shell, "That's a PuTTY-format public key. In PuTTYgen, copy the line under " +
                                           "\"Public key for pasting into OpenSSH authorized_keys file\" into a .pub file instead.", Title);
            return;
        }

        var result = await Task.Run(() => AuthorizedKeysInstaller.Install(client, line));
        if (!result.Success)
        {
            UiHelpers.ShowError(ctx.Shell, result.Message, Title);
            return;
        }

        // Offer to switch the saved session to this key (private key = same path without .pub).
        var privatePath = dlg.FileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? dlg.FileName[..^4] : null;
        var profile = AppServices.Profiles.Find(tab.Profile.Id);
        if (privatePath is not null && File.Exists(privatePath) && profile is not null &&
            !(profile.Auth == AuthMethod.PrivateKey && string.Equals(profile.PrivateKeyPath, privatePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (MessageBox.Show(ctx.Shell, result.Message + $"\n\nSwitch the session '{profile.DisplayName}' to log in with {Path.GetFileName(privatePath)}?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                profile.Auth = AuthMethod.PrivateKey;
                profile.PrivateKeyPath = privatePath;
                AppServices.Profiles.Upsert(profile);
                MessageBox.Show(ctx.Shell, "Done. Open a new connection to test it — keep this one open until it works.",
                                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        MessageBox.Show(ctx.Shell, result.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>Adds (or removes) a coloured user@host:path prompt in the login user's shell start-up file.</summary>
public sealed class ColorPromptFeature : IAppFeature
{
    public string Title => "Colour prompt";
    public string Glyph => "";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Colour the user@host:path prompt on this server, using the terminal's colour scheme (or remove it again)";

    public async void Execute(FeatureContext ctx)
    {
        var tab = ctx.Terminal;
        if (tab?.Client is not { } client)
        {
            MessageBox.Show(ctx.Shell, "Connect first, then try again.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<string> installed;
        PromptShellInfo shell;
        try
        {
            (installed, shell) = await Task.Run(() => (ColorPromptInstaller.FindInstalled(client), ColorPromptInstaller.Detect(client)));
        }
        catch (Exception ex) { UiHelpers.ShowError(ctx.Shell, ex.Message, Title); return; }

        var who = $"{tab.Profile.Username} on {tab.Profile.Host}";
        PromptResult result;
        if (installed.Count > 0)
        {
            if (MessageBox.Show(ctx.Shell, $"Fjord's coloured prompt is set up for {who} (in {string.Join(", ", installed)}).\n\nRemove it?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            result = await Task.Run(() => ColorPromptInstaller.Remove(client));
            Report(ctx, result, applyNow: false);
            return;
        }

        if (shell.Kind == PromptShell.Unsupported)
        {
            MessageBox.Show(ctx.Shell, shell.Problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(ctx.Shell,
                $"Add a coloured prompt for {who}?\n\n" +
                $"Fjord adds a small, clearly marked block to {shell.RcFile}: user@host in green (red when you're root) and the folder " +
                "in blue — the colours of your terminal colour scheme.\n\n" +
                "It replaces this account's own prompt on this server. Click Colour prompt again any time to remove it.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        result = await Task.Run(() => ColorPromptInstaller.Install(client, shell));
        Report(ctx, result, applyNow: true);
    }

    private void Report(FeatureContext ctx, PromptResult result, bool applyNow)
    {
        if (!result.Success) { UiHelpers.ShowError(ctx.Shell, result.Message, Title); return; }
        if (applyNow && result.ReloadCommand is { } reload && ctx.Terminal?.Client is not null)
        {
            if (MessageBox.Show(ctx.Shell, result.Message + $"\n\nUse it in this terminal now? (Fjord types: {reload})",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                ctx.Terminal.Send(" " + reload + "\r");   // leading space: kept out of shell history where supported
            return;
        }
        MessageBox.Show(ctx.Shell, result.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>Session log: start/stop writing terminal output to a file, open it, or go to the log folder.</summary>
public sealed class LogFeature : IAppFeature
{
    public string Title => "Log";
    public string Glyph => "";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Save everything this terminal shows to a file";

    public void Execute(FeatureContext ctx)
    {
        if (ctx.Terminal is not { } tab) return;
        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (tab.IsLogging)
        {
            Add("Stop logging", () => tab.StopLogging());
            Add("Open the log", () => OpenFile(ctx, tab.LogPath!));
            Add("Show the log in Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tab.LogPath}\"")));
        }
        else
        {
            Add("Start logging to a file", () => tab.StartLogging());
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Open the log folder", () =>
        {
            var folder = Environment.ExpandEnvironmentVariables(AppServices.Settings.LogFolder);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        });
        Add("Log settings…", () => new SettingsDialog { Owner = ctx.Shell }.ShowDialog());
        menu.IsOpen = true;
    }

    private static void OpenFile(FeatureContext ctx, string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { UiHelpers.ShowError(ctx.Shell, ex.Message, "Session log"); }
    }
}

public sealed class DuplicateSessionFeature : IAppFeature
{
    public string Title => "";
    public string Glyph => "\uE8A7";
    public FeatureScope Scope => FeatureScope.Session;
    public string? ToolTip => "Open another terminal to this server (Ctrl+Shift+T)";

    public void Execute(FeatureContext ctx)
    {
        if (ctx.Terminal is not null) ctx.Shell.OpenTerminal(ctx.Terminal.Profile);
    }
}

// ───────────────────────── sidebar tools ─────────────────────────

public sealed class KeyGeneratorFeature : IAppFeature
{
    public string Title => "Key generator";
    public string Glyph => "\uE8D7";
    public FeatureScope Scope => FeatureScope.Global;
    public void Execute(FeatureContext ctx) => new KeyGeneratorWindow { Owner = ctx.Shell }.Show();
}

public sealed class KeyGuideFeature : IAppFeature
{
    public string Title => "SSH keys — guide";
    public string Glyph => "\uE897";
    public FeatureScope Scope => FeatureScope.Global;
    public void Execute(FeatureContext ctx) => GuideWindow.ShowSingle(ctx.Shell);
}

public sealed class TunnelGuideFeature : IAppFeature
{
    public string Title => "Tunnels — guide";
    public string Glyph => "\uE897";
    public FeatureScope Scope => FeatureScope.Global;
    public void Execute(FeatureContext ctx) => GuideWindow.ShowSingle(ctx.Shell, GuideWindow.TunnelsPage);
}

public sealed class ImportPuttyFeature : IAppFeature
{
    public string Title => "Import from PuTTY";
    public string Glyph => "\uE8B5";
    public FeatureScope Scope => FeatureScope.Global;

    public void Execute(FeatureContext ctx)
    {
        var found = PuttyImporter.ReadSessions();
        var existing = AppServices.Profiles.All.Select(p => (p.Host.ToLowerInvariant(), p.Port, p.Username)).ToHashSet();
        var fresh = found.Where(p => !existing.Contains((p.Host.ToLowerInvariant(), p.Port, p.Username))).ToList();

        if (fresh.Count == 0)
        {
            MessageBox.Show(ctx.Shell, found.Count == 0 ? "No saved PuTTY SSH sessions found on this PC." : "All PuTTY sessions are already imported.",
                            Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var list = string.Join("\n", fresh.Take(15).Select(p => $"• {p.Name}  ({p.Target})")) + (fresh.Count > 15 ? $"\n… and {fresh.Count - 15} more" : "");
        if (MessageBox.Show(ctx.Shell, $"Import {fresh.Count} session(s) into the group \"Imported from PuTTY\"?\n\n{list}",
                            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            AppServices.Profiles.AddRange(fresh);
    }
}

public sealed class SettingsFeature : IAppFeature
{
    public string Title => "Settings";
    public string Glyph => "\uE713";
    public FeatureScope Scope => FeatureScope.Global;
    public void Execute(FeatureContext ctx) => new SettingsDialog { Owner = ctx.Shell }.ShowDialog();
}
