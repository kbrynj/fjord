using System.Runtime.InteropServices;
using System.Windows;
using Fjord.Core.Sftp;
using Renci.SshNet;

namespace Fjord.Views;

/// <summary>
/// Cut / copy / paste on the server (also between SFTP tabs of the same server), and moving by
/// dragging onto a folder. Copies run <c>cp -a</c> over SSH when possible — the data stays on the server.
/// </summary>
public partial class SftpTab
{
    private sealed record FileClipboard(SftpTab Source, IReadOnlyList<RemoteEntry> Entries, bool Cut, uint Sequence);

    /// <summary>Shared by all SFTP tabs, so you can cut in one tab and paste in another tab of the same server.</summary>
    private static FileClipboard? s_clipboard;

    /// <summary>Extra SSH connection used to run cp. Opened on first copy, reusing the login.</summary>
    private SshClient? _shellClient;
    private bool _shellUnavailable;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private void Cut_Click(object sender, RoutedEventArgs e) => PutOnClipboard(cut: true);
    private void Copy_Click(object sender, RoutedEventArgs e) => PutOnClipboard(cut: false);

    private void PutOnClipboard(bool cut)
    {
        var items = Selected;
        if (items.Count == 0) return;
        // The paths also go on the Windows clipboard as text — handy for pasting into a terminal.
        try { Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.FullName))); } catch { }
        s_clipboard = new FileClipboard(this, items, cut, GetClipboardSequenceNumber());
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        SetStatus($"{(cut ? "Cut" : "Copied")} {what} — go to another folder and press Ctrl+V to {(cut ? "move" : "copy")} {(items.Count == 1 ? "it" : "them")} there.");
    }

    /// <summary>
    /// Ctrl+V: files cut/copied in Fjord are pasted on the server. If files were copied in Explorer
    /// since then, those are uploaded instead.
    /// </summary>
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var clip = s_clipboard;
        bool windowsClipboardIsNewer = clip is null || GetClipboardSequenceNumber() != clip.Sequence;
        if (windowsClipboardIsNewer && TryGetClipboardFiles() is { Count: > 0 } localFiles)
        {
            await UploadAsync(localFiles);
            return;
        }
        if (clip is null)
        {
            SetStatus("Nothing to paste. Select files and press Ctrl+C or Ctrl+X first (or copy files in Explorer).");
            return;
        }
        if (!SameServer(clip.Source))
        {
            SetStatus("Paste only works within the same server. To copy between servers, drag the files onto the other tab.");
            return;
        }

        if (await TransferAsync(clip.Entries, _path, clip.Cut) && clip.Cut)
        {
            s_clipboard = null;   // moved; pasting again would fail
            if (clip.Source != this && !clip.Source._closed) _ = clip.Source.RefreshAsync();
        }
    }

    private static List<string>? TryGetClipboardFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : null;
        }
        catch { return null; }
    }

    private bool SameServer(SftpTab other) =>
        other == this ||
        string.Equals(other.Profile.Host, Profile.Host, StringComparison.OrdinalIgnoreCase) &&
        other.Profile.Port == Profile.Port && other.Profile.Username == Profile.Username;

    /// <summary>Moves or copies <paramref name="items"/> into <paramref name="destDir"/> on this server.</summary>
    /// <returns>true if everything was done.</returns>
    private async Task<bool> TransferAsync(IReadOnlyList<RemoteEntry> items, string destDir, bool move)
    {
        if (_client is null || items.Count == 0) return false;
        var verb = move ? "move" : "copy";

        if (items.FirstOrDefault(i => i.IsDirectory && RemoteFileOps.IsSameOrInside(destDir, i.FullName)) is { } loop)
        {
            SetStatus($"✖ Can't {verb} the folder '{loop.Name}' into itself.");
            return false;
        }
        // Moving into the folder it's already in does nothing.
        var work = move ? items.Where(i => ParentPath(i.FullName) != destDir.TrimEnd('/') && ParentPath(i.FullName) != destDir).ToList() : [.. items];
        if (work.Count == 0) return true;

        // Plan the destination names and find what would be overwritten (one prompt for all).
        var plan = new List<(RemoteEntry Item, string Target, bool Replace)>();
        var planned = await RunAsync("Checking…", (client, ct) => Task.Run(() =>
        {
            foreach (var item in work)
            {
                var sameFolder = ParentPath(item.FullName) == ParentPath(Combine(destDir, item.Name));
                var name = !move && sameFolder ? RemoteFileOps.UniqueCopyName(client, destDir, item.Name) : item.Name;
                var target = Combine(destDir, name);
                plan.Add((item, target, client.Exists(target)));
            }
        }, ct));
        if (!planned) return false;

        var clashes = plan.Where(p => p.Replace).ToList();
        if (clashes.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                $"{clashes.Count} item(s) already exist in {destDir} and will be replaced:\n\n" +
                string.Join("\n", clashes.Take(10).Select(c => RemoteFileOps.NameOf(c.Target))),
                "Replace?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;

        if (!move) await EnsureShellClientAsync();
        var shell = _shellClient;

        bool serverSide = true;
        var done = await RunAsync($"{(move ? "Moving" : "Copying")} {work.Count} item(s)…", (client, ct) => Task.Run(() =>
        {
            foreach (var (item, target, replace) in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (replace) RemoteFileOps.DeleteRecursive(client, target, client.GetAttributes(target).IsDirectory, ct);
                if (move)
                    RemoteFileOps.Move(client, item.FullName, target);
                else
                    serverSide &= RemoteFileOps.Copy(shell, client, item.FullName, target, item.IsDirectory, ct,
                        name => Dispatcher.InvokeAsync(() => SetStatus($"Copying {name} (through this PC — the server has no cp)…")));
            }
        }, ct));

        await NavigateAsync(_path, select: destDir == _path && plan.Count == 1 ? RemoteFileOps.NameOf(plan[0].Target) : null);
        if (done)
            SetStatus($"{(move ? "Moved" : "Copied")} {plan.Count} item(s) to {destDir}" + (move || serverSide ? "." : " (streamed through this PC)."));
        return done;
    }

    private async Task EnsureShellClientAsync()
    {
        if (_shellClient is { IsConnected: true } || _shellUnavailable) return;
        try
        {
            SetStatus("Connecting a command channel for copying…");
            _shellClient?.Dispose();
            _shellClient = await _endpoint.ConnectSshAsync();
        }
        catch
        {
            _shellUnavailable = true;   // e.g. SFTP-only account → copies stream through this PC
            _shellClient = null;
        }
    }
}
