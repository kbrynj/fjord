using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Fjord.Views;

/// <summary>
/// "Open" a remote file = download a local copy, open it in its Windows app, and watch it.
/// Every save in the editor uploads it back (WinSCP-style). If the file changed on the server in
/// the meantime, you're asked before it's overwritten.
/// </summary>
public partial class SftpTab
{
    private sealed class EditedFile
    {
        public required string Remote { get; init; }
        public required string Local { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public required DispatcherTimer Debounce { get; init; }
        public string Name => Path.GetFileName(Local);
        /// <summary>What the server had when we last downloaded/uploaded — to detect someone else's changes.</summary>
        public DateTime RemoteWriteUtc { get; set; }
        public long RemoteSize { get; set; }
        /// <summary>Hash of the content the server has from us; skips uploads when an editor saves without changes.</summary>
        public byte[] SyncedHash { get; set; } = [];
    }

    private readonly Dictionary<string, EditedFile> _edits = new(StringComparer.Ordinal);   // by remote path
    private readonly string _editFolder = Path.Combine(TempRoot("edit"), Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Files Windows would run rather than open — never start these directly from a server.</summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".scr", ".pif", ".cpl", ".lnk", ".reg", ".jar", ".appref-ms", ".application",
    };

    private async Task OpenForEditingAsync(RemoteEntry entry, bool chooseApp)
    {
        if (_client is null) return;

        if (!_edits.TryGetValue(entry.FullName, out var edit))
        {
            // One sub-folder per remote folder, so two "config.yml" from different places don't collide.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ParentPath(entry.FullName))))[..10];
            var dir = Path.Combine(_editFolder, hash);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeFileName(entry.Name));

            var ok = await RunAsync($"Opening {entry.Name}…", (client, ct) => Task.Run(() => DownloadFile(client, entry.FullName, local, null, ct), ct));
            if (!ok) return;

            edit = StartWatching(entry.FullName, local);
            await RecordServerStateAsync(edit, File.ReadAllBytes(local));
        }
        else if (!await RefreshIfUnchangedLocallyAsync(edit))
        {
            return;
        }

        Launch(edit.Local, chooseApp || RunnableExtensions.Contains(Path.GetExtension(edit.Local)));
        SetStatus($"Editing {edit.Name} — each time you save it in the editor, it's uploaded to the server.");
        UpdateEditingBadge();
    }

    private EditedFile StartWatching(string remote, string local)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(local)!, Path.GetFileName(local))
        {
            // Editors save in different ways: write in place, or write a temp file and rename it over the original.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        var edit = new EditedFile
        {
            Remote = remote,
            Local = local,
            Watcher = watcher,
            Debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) },
        };
        edit.Debounce.Tick += async (_, _) =>
        {
            edit.Debounce.Stop();
            await UploadEditAsync(edit);
        };

        void OnChange(object? sender, FileSystemEventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            if (_closed) return;
            edit.Debounce.Stop();   // restart: wait until the editor has finished writing
            edit.Debounce.Start();
        });
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Renamed += (s, e) => { if (string.Equals(e.FullPath, local, StringComparison.OrdinalIgnoreCase)) OnChange(s, e); };
        watcher.EnableRaisingEvents = true;

        _edits[remote] = edit;
        return edit;
    }

    /// <summary>Re-opening a file: fetch the server's newer version, unless there are local edits not uploaded yet.</summary>
    private async Task<bool> RefreshIfUnchangedLocallyAsync(EditedFile edit)
    {
        if (!File.Exists(edit.Local)) return await RedownloadAsync(edit);

        var (changedOnServer, _) = await CheckServerAsync(edit);
        if (!changedOnServer) return true;

        var localHash = SHA256.HashData(ReadShared(edit.Local));
        if (localHash.AsSpan().SequenceEqual(edit.SyncedHash)) return await RedownloadAsync(edit);

        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{edit.Name} has changed on the server, and your local copy also has changes that weren't uploaded.\n\n" +
            "Yes = open the server's version (your local changes are discarded)\nNo = keep editing your version",
            "Changed on both sides", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => await RedownloadAsync(edit),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private async Task<bool> RedownloadAsync(EditedFile edit)
    {
        edit.Watcher.EnableRaisingEvents = false;
        try
        {
            var ok = await RunAsync($"Opening {edit.Name}…", (client, ct) => Task.Run(() => DownloadFile(client, edit.Remote, edit.Local, null, ct), ct));
            if (ok) await RecordServerStateAsync(edit, File.ReadAllBytes(edit.Local));
            return ok;
        }
        finally
        {
            edit.Watcher.EnableRaisingEvents = !_closed;
        }
    }

    private async Task UploadEditAsync(EditedFile edit)
    {
        if (_closed || _client is null) return;

        byte[] content;
        try { content = await Task.Run(() => ReadShared(edit.Local)); }
        catch (FileNotFoundException) { return; }   // editor is mid-rename; the Created/Renamed event will follow
        catch (IOException ex) { SetStatus($"✖ Couldn't read {edit.Name}: {ex.Message}"); return; }

        var hash = SHA256.HashData(content);
        if (hash.AsSpan().SequenceEqual(edit.SyncedHash)) return;   // saved without changes

        var (changedOnServer, exists) = await CheckServerAsync(edit);
        if (changedOnServer)
        {
            var what = exists ? "was changed on the server by someone else" : "was deleted or moved on the server";
            if (MessageBox.Show(Window.GetWindow(this),
                    $"{edit.Name} {what} since you opened it.\n\nUpload your version anyway? " +
                    (exists ? "This overwrites the server's changes." : "This puts the file back."),
                    "Upload " + edit.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                SetStatus($"{edit.Name} was not uploaded. Save again to retry, or re-open it to get the server's version.");
                return;
            }
        }

        var ok = await RunAsync($"Uploading {edit.Name}…", (client, ct) => Task.Run(() =>
        {
            using var input = new MemoryStream(content);
            client.UploadFile(input, edit.Remote, canOverride: true);   // writes in place → keeps the file's owner and permissions
        }, ct), wait: true);

        if (!ok)
        {
            SetStatus($"✖ {edit.Name} was NOT uploaded — save again in the editor to retry. ({StatusText.Text.TrimStart('✖', ' ')})");
            return;
        }
        await RecordServerStateAsync(edit, content);
        SetStatus($"✔ Saved {edit.Name} to the server at {DateTime.Now:HH:mm:ss}.");
        await RefreshAsync();
    }

    /// <returns>changed: the server's file differs from what we last saw; exists: it's still there.</returns>
    private async Task<(bool Changed, bool Exists)> CheckServerAsync(EditedFile edit)
    {
        var client = _client;
        if (client is null) return (false, true);
        try
        {
            var attrs = await Task.Run(() => client.GetAttributes(edit.Remote));
            return (attrs.LastWriteTimeUtc != edit.RemoteWriteUtc || attrs.Size != edit.RemoteSize, true);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return (true, false);
        }
        catch
        {
            return (false, true);   // can't tell (connection trouble) — the upload itself will report the error
        }
    }

    private async Task RecordServerStateAsync(EditedFile edit, byte[] content)
    {
        edit.SyncedHash = SHA256.HashData(content);
        try
        {
            var client = _client!;
            var attrs = await Task.Run(() => client.GetAttributes(edit.Remote));
            edit.RemoteWriteUtc = attrs.LastWriteTimeUtc;
            edit.RemoteSize = attrs.Size;
        }
        catch { /* next check will just ask */ }
    }

    /// <summary>Reads a file an editor may still have open (and retries briefly while it's locked).</summary>
    private static byte[] ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                Thread.Sleep(150);
            }
        }
    }

    private static void Launch(string local, bool chooseApp)
    {
        if (!chooseApp)
        {
            try
            {
                Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
                return;
            }
            catch (System.ComponentModel.Win32Exception) { /* no app for this file type → let the user pick one */ }
        }
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {local}") { UseShellExecute = false });
    }

    private void UpdateEditingBadge()
    {
        EditingBadge.Visibility = _edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditingText.Text = _edits.Count == 1 ? $"Editing {_edits.Values.First().Name}" : $"Editing {_edits.Count} files";
        EditingBadge.ToolTip = "Saving these in their editor uploads them to the server:\n" +
                               string.Join("\n", _edits.Keys.Order(StringComparer.Ordinal));
    }

    private void StopEditing()
    {
        foreach (var edit in _edits.Values)
        {
            edit.Debounce.Stop();
            edit.Watcher.Dispose();
        }
        _edits.Clear();
    }
}
