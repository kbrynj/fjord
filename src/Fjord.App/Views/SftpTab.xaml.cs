using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fjord.Core.Profiles;
using Fjord.Core.Sftp;
using Fjord.Core.Ssh;
using Fjord.Dialogs;
using Fjord.Services;
using Microsoft.Win32;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Fjord.Views;

/// <summary>One row in the remote file list.</summary>
public sealed class RemoteEntry
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    /// <summary>Unix permission bits, e.g. 0x1ED (= 755).</summary>
    public int Mode { get; init; }
    public int Owner { get; init; }
    public int Group { get; init; }

    public string Permissions => (IsDirectory ? "d" : IsLink ? "l" : "-") + UnixMode.ToSymbolic(Mode);
    public string Icon => IsDirectory ? "" : IsLink ? "" : "";
    public Brush IconBrush => IsDirectory ? FolderBrush : Brushes.Gray;
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    /// <summary>Folders first, then by name.</summary>
    public string SortKey => (IsDirectory ? "0" : "1") + Name.ToLowerInvariant();

    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39));

    public static string FormatSize(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} B" : $"{bytes:0.#} {units[u]}";
    }
}

/// <summary>
/// SFTP browser / remote file manager. Can open on its own (new connection) or from a terminal tab, reusing
/// that session's credentials so you aren't asked for the password twice.
/// Split over partial files: editing (open → save uploads), move/copy (cut/copy/paste, drag onto folders),
/// drag-out (drag to Explorer downloads).
/// </summary>
public partial class SftpTab : UserControl, ISessionTab
{
    private readonly MainWindow _shell;
    private readonly SshEndpoint _endpoint;
    private readonly bool _ownsEndpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);   // SFTP operations run one at a time
    private SftpClient? _client;
    private CancellationTokenSource? _opCts;
    private string _path = "/";
    private bool _closed;

    public SessionProfile Profile { get; }
    public string Title => "SFTP · " + Profile.DisplayName;
    public string Glyph => "";
    // The SFTP tab title never changes, so the event is never raised.
    public event EventHandler? TitleChanged { add { } remove { } }

    public SftpTab(MainWindow shell, SessionProfile profile, SshEndpoint? sharedEndpoint = null)
    {
        InitializeComponent();
        _shell = shell;
        Profile = profile;
        _ownsEndpoint = sharedEndpoint is null;
        _endpoint = sharedEndpoint ?? new SshEndpoint(profile, new UiPrompts(shell), AppServices.KnownHosts, AppServices.Protector);

        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Up one folder (Backspace)", (_, _) => _ = NavigateAsync(ParentPath(_path))));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Home folder", (_, _) => _ = NavigateAsync(_client?.WorkingDirectory ?? "/")));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Refresh (F5)", (_, _) => _ = NavigateAsync(_path)));

        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", "New folder", null, NewFolder_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", "Upload", "Upload files (or drag them in from Explorer)", Upload_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", "Download", "Download selected (or drag them out to Explorer)", Download_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Delete selected (Del)", Delete_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Open a terminal to this server", (_, _) => _shell.OpenTerminal(Profile)));

        InitLocalPane();
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        SetStatus($"Connecting to {Profile.Target}…");
        CleanUpOldTempFolders();
        try
        {
            _client = await _endpoint.ConnectSftpAsync();
            if (_closed) { _client.Dispose(); return; }
            if (_ownsEndpoint && _endpoint.ProfileModified && AppServices.Profiles.Find(Profile.Id) is not null)
                AppServices.Profiles.Upsert(Profile);

            var start = string.IsNullOrWhiteSpace(Profile.SftpStartPath) ? _client.WorkingDirectory : Profile.SftpStartPath!;
            await NavigateAsync(start);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) { SetStatus("✖ " + ex.Message); }
    }

    // ───────────────────────── navigation ─────────────────────────

    private async Task NavigateAsync(string path, string? select = null)
    {
        if (_client is null) return;
        await RunAsync("Loading…", async (client, ct) =>
        {
            var entries = await Task.Run(() => client.ListDirectory(path)
                .Where(f => f.Name is not "." and not "..")
                .Select(ToEntry)
                .OrderBy(x => x.SortKey, StringComparer.Ordinal)
                .ToList(), ct);

            _path = path;
            PathBox.Text = path;
            FileGrid.ItemsSource = entries;
            if (select is not null && entries.FirstOrDefault(x => x.Name == select) is { } item)
            {
                FileGrid.SelectedItem = item;
                FileGrid.ScrollIntoView(item);
            }
            SetStatus($"{entries.Count(x => x.IsDirectory)} folders, {entries.Count(x => !x.IsDirectory)} files");
        });
    }

    private Task RefreshAsync() => NavigateAsync(_path);

    private static RemoteEntry ToEntry(ISftpFile f) => new()
    {
        Name = f.Name,
        FullName = f.FullName,
        IsDirectory = f.IsDirectory,
        IsLink = f.IsSymbolicLink,
        Size = f.Length,
        Modified = f.LastWriteTime,
        Mode = UnixMode.FromAttributes(f.Attributes),
        Owner = f.UserId,
        Group = f.GroupId,
    };

    private static string Combine(string dir, string name) => RemoteFileOps.Combine(dir, name);
    private static string ParentPath(string path) => RemoteFileOps.ParentPath(path);

    private List<RemoteEntry> Selected => FileGrid.SelectedItems.Cast<RemoteEntry>().ToList();
    private IEnumerable<RemoteEntry> Listed => FileGrid.ItemsSource as IEnumerable<RemoteEntry> ?? [];

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = NavigateAsync(PathBox.Text.Trim());
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Open_Click(sender, e); e.Handled = true; break;
            case Key.Back: _ = NavigateAsync(ParentPath(_path)); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.F5: _ = RefreshAsync(); e.Handled = true; break;
            case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
        }
    }

    /// <summary>Ctrl+X/C/V are handled before the DataGrid, which would otherwise copy cell text.</summary>
    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.X: Cut_Click(sender, e); e.Handled = true; break;
            case Key.C: Copy_Click(sender, e); e.Handled = true; break;
            case Key.V: Paste_Click(sender, e); e.Handled = true; break;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileGrid, d) is DataGridRow)
            Open_Click(sender, e);
    }

    /// <summary>Folders: navigate. Files: open in their Windows app; saving uploads them (see SftpTab.Editing.cs).</summary>
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not RemoteEntry entry) return;
        if (entry.IsDirectory || entry.IsLink && await IsDirectoryAsync(entry.FullName))
        {
            await NavigateAsync(entry.FullName);
            return;
        }
        await OpenForEditingAsync(entry, chooseApp: false);
    }

    private async void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is RemoteEntry { IsDirectory: false } entry)
            await OpenForEditingAsync(entry, chooseApp: true);
    }

    private async Task<bool> IsDirectoryAsync(string path)
    {
        if (_client is null) return false;
        try { return await Task.Run(() => _client.Get(path).IsDirectory); }
        catch { return false; }
    }

    // ───────────────────────── upload / download ─────────────────────────

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        // Two panes: upload what's selected on the left, if anything.
        if (TwoPane && Local.Selected.Count > 0)
        {
            _ = UploadAsync(Local.Selected.Select(x => x.FullPath).ToList());
            return;
        }
        var dlg = new OpenFileDialog { Title = $"Upload to {_path}", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            _ = UploadAsync(dlg.FileNames);
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_client is null) return;
        var existing = Listed.Select(x => x.Name).ToHashSet();
        var clashes = localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))).Where(existing.Contains).ToList();
        if (clashes.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                $"{clashes.Count} item(s) already exist in {_path} and will be overwritten:\n\n{string.Join("\n", clashes.Take(10))}",
                "Overwrite?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var files = new List<(string Local, string Remote, long Size)>();
        var dirs = new List<string>();
        foreach (var p in localPaths)
            CollectUploads(p, _path, files, dirs);
        long total = Math.Max(1, files.Sum(f => f.Size));

        await RunAsync($"Uploading {files.Count} file(s)…", async (client, ct) =>
        {
            long done = 0;
            await Task.Run(() =>
            {
                foreach (var d in dirs)
                    if (!client.Exists(d)) client.CreateDirectory(d);

                foreach (var (local, remote, size) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    long before = done;
                    using var input = File.OpenRead(local);
                    client.UploadFile(input, remote, canOverride: true, uploaded =>
                    {
                        ReportProgress((before + (long)uploaded) * 100.0 / total, $"Uploading {Path.GetFileName(local)}");
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    });
                    done += size;
                }
            }, ct);
            SetStatus($"Uploaded {files.Count} file(s), {RemoteEntry.FormatSize(done)}.");
        });
        await RefreshAsync();
    }

    private static void CollectUploads(string localPath, string remoteDir, List<(string, string, long)> files, List<string> dirs)
    {
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        var remote = Combine(remoteDir, name);
        if (Directory.Exists(localPath))
        {
            dirs.Add(remote);
            foreach (var entry in Directory.EnumerateFileSystemEntries(localPath))
                CollectUploads(entry, remote, files, dirs);
        }
        else if (File.Exists(localPath))
        {
            files.Add((localPath, remote, new FileInfo(localPath).Length));
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0 || _client is null) return;

        // Two panes: download straight into the folder shown on the left.
        if (TwoPane && Local.CurrentFolder.Length > 0)
        {
            if (await DownloadToFolderAsync(items, Local.CurrentFolder))
                Local.Refresh(select: items.Count == 1 ? SafeFileName(items[0].Name) : null);
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = "Download to…",
            InitialDirectory = AppServices.Settings.LastDownloadFolder ??
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = dlg.FolderName;
        AppServices.Settings.LastDownloadFolder = target;
        AppServices.Settings.Save();

        await DownloadToFolderAsync(items, target);
    }

    /// <summary>Downloads files and folders (recursively) into a local folder. Blocking — call off the UI thread.</summary>
    private (int Files, long Bytes) DownloadTree(SftpClient client, IEnumerable<RemoteEntry> items, string localDir, CancellationToken ct)
    {
        var files = new List<(string Remote, string Local, long Size)>();
        foreach (var item in items) CollectDownloads(client, item.FullName, item.IsDirectory, localDir, files, ct);

        long total = Math.Max(1, files.Sum(f => f.Size)), done = 0;
        foreach (var (remote, local, size) in files)
        {
            long before = done;
            DownloadFile(client, remote, local, b => ReportProgress((before + b) * 100.0 / total, $"Downloading {Path.GetFileName(local)}"), ct);
            done += size;
        }
        return (files.Count, done);
    }

    private static void CollectDownloads(SftpClient client, string remote, bool isDir, string localDir, List<(string, string, long)> list, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var local = Path.Combine(localDir, SafeFileName(RemoteFileOps.NameOf(remote)));
        if (isDir)
        {
            Directory.CreateDirectory(local);
            foreach (var f in client.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
                CollectDownloads(client, f.FullName, f.IsDirectory, local, list, ct);
        }
        else
        {
            list.Add((remote, local, client.Get(remote).Length));
        }
    }

    /// <summary>Downloads to "name.fjord-part" first, so a cancelled download never leaves a half file under the real name.</summary>
    private static void DownloadFile(SftpClient client, string remote, string local, Action<long>? progress, CancellationToken ct)
    {
        var tmp = local + ".fjord-part";
        try
        {
            using (var output = File.Create(tmp))
            {
                client.DownloadFile(remote, output, downloaded =>
                {
                    progress?.Invoke((long)downloaded);
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                });
            }
            File.Move(tmp, local, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ───────────────────────── file operations ─────────────────────────

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("New folder", $"Folder name in {_path}:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        var name = dlg.Value.Trim();
        await RunAsync("Creating folder…", (client, ct) => Task.Run(() => client.CreateDirectory(Combine(_path, name)), ct));
        await NavigateAsync(_path, select: name);
    }

    /// <summary>Creates an empty file and opens it in its editor.</summary>
    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("New file", $"File name in {_path}:", initialValue: "new-file.txt") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        var name = dlg.Value.Trim();
        var path = Combine(_path, name);
        var created = await RunAsync("Creating file…", (client, ct) => Task.Run(() =>
        {
            if (client.Exists(path)) throw new IOException($"'{name}' already exists.");
            client.Create(path).Dispose();
        }, ct));
        if (!created) return;
        await NavigateAsync(_path, select: name);
        if (Listed.FirstOrDefault(x => x.Name == name) is { } entry)
            await OpenForEditingAsync(entry, chooseApp: false);
    }

    /// <summary>Rename, or move when the new name is a path ("../archive/x.log", "/srv/backup/").</summary>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not RemoteEntry entry || _client is null) return;
        var dlg = new InputDialog("Rename or move",
            $"New name for {entry.Name}.\nTo move it, type a folder path, e.g. ../archive/ or /var/backups/{entry.Name}",
            initialValue: entry.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value) || dlg.Value.Trim() == entry.Name) return;
        var input = dlg.Value.Trim();

        await RunAsync("Moving…", async (client, ct) =>
        {
            var target = RemoteFileOps.ResolveTarget(_path, input);
            // "folder/" or an existing folder → move into it, keeping the name.
            bool intoFolder = input.EndsWith('/') || await Task.Run(() => client.Exists(target) && client.GetAttributes(target).IsDirectory, ct);
            if (intoFolder) target = Combine(target, entry.Name);
            if (target == entry.FullName) return;
            if (entry.IsDirectory && RemoteFileOps.IsSameOrInside(target, entry.FullName))
                throw new IOException("A folder can't be moved into itself.");

            if (await Task.Run(() => client.Exists(target), ct))
            {
                if (MessageBox.Show(Window.GetWindow(this), $"{target} already exists. Replace it?", "Replace?",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                await Task.Run(() => RemoteFileOps.DeleteRecursive(client, target, client.GetAttributes(target).IsDirectory, ct), ct);
            }
            await Task.Run(() => RemoteFileOps.Move(client, entry.FullName, target), ct);
            SetStatus(ParentPath(target) == _path ? $"Renamed to {RemoteFileOps.NameOf(target)}." : $"Moved to {target}");
        });
        await RefreshAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (items.Any(i => i.IsDirectory)) what += " (folders are deleted with everything inside)";
        if (MessageBox.Show(Window.GetWindow(this), $"Permanently delete {what}?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunAsync("Deleting…", (client, ct) => Task.Run(() =>
        {
            foreach (var item in items) RemoteFileOps.DeleteRecursive(client, item.FullName, item.IsDirectory, ct);
        }, ct));
        await RefreshAsync();
    }

    private async void Permissions_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected.Where(i => !i.IsLink).ToList();   // a symlink's own mode is meaningless
        if (items.Count == 0) return;

        var first = items[0];
        var target = items.Count == 1 ? first.Name : $"{items.Count} items";
        var owner = items.Count == 1 ? $"Owner user id {first.Owner}, group id {first.Group}" : "The settings below are applied to all of them.";
        var dlg = new PermissionsDialog(target, owner, first.Mode, items.Any(i => i.IsDirectory), items.All(i => i.IsDirectory))
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        int mode = dlg.Mode;
        bool recursive = dlg.Recursive;
        await RunAsync("Changing permissions…", async (client, ct) =>
        {
            var count = await Task.Run(() => items.Sum(i => RemoteFileOps.ChangeMode(client, i.FullName, i.IsDirectory, mode, recursive, ct)), ct);
            SetStatus($"Permissions set to {UnixMode.ToOctal(mode)} ({UnixMode.ToSymbolic(mode)}) on {count} item(s).");
        });
        await NavigateAsync(_path, select: items.Count == 1 ? first.Name : null);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var text = FileGrid.SelectedItem is RemoteEntry entry ? entry.FullName : _path;
        try { Clipboard.SetText(text); } catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _opCts?.Cancel();

    // ───────────────────────── plumbing ─────────────────────────

    /// <summary>
    /// Runs one SFTP operation at a time with busy UI, cancel button and error reporting.
    /// <paramref name="wait"/>: queue behind a running operation instead of refusing (used for background uploads).
    /// </summary>
    /// <returns>true if the operation completed.</returns>
    private async Task<bool> RunAsync(string description, Func<SftpClient, CancellationToken, Task> op, bool wait = false)
    {
        var client = _client;
        if (client is null || _closed) return false;
        if (wait) await _gate.WaitAsync();
        else if (!await _gate.WaitAsync(0))
        {
            SetStatus("Busy — wait for the current operation to finish.");
            return false;
        }

        _opCts = new CancellationTokenSource();
        SetStatus(description);
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        try
        {
            await op(client, _opCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("✖ " + ex.Message);
            if (!client.IsConnected) SetStatus("✖ Connection lost. Close this tab and open SFTP again.");
        }
        finally
        {
            Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            _gate.Release();
        }
        return false;
    }

    private DateTime _lastProgress;

    private void ReportProgress(double percent, string text)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgress).TotalMilliseconds < 100 && percent < 100) return;   // don't flood the UI thread
        _lastProgress = now;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100, percent);
            StatusText.Text = $"{text} — {percent:0}%";
        }));
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>%TEMP%\Fjord\&lt;kind&gt; — local copies for editing and for dragging out.</summary>
    private static string TempRoot(string kind) => Path.Combine(Path.GetTempPath(), "Fjord", kind);

    /// <summary>Drag-out copies are only needed until Explorer has copied them; edit copies are kept for a week.</summary>
    private static void CleanUpOldTempFolders()
    {
        foreach (var (kind, age) in new[] { ("drag", TimeSpan.FromDays(1)), ("edit", TimeSpan.FromDays(7)) })
        {
            try
            {
                var root = new DirectoryInfo(TempRoot(kind));
                if (!root.Exists) continue;
                foreach (var dir in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                    if (DateTime.Now - dir.LastWriteTime > age)
                        try { dir.Delete(recursive: true); } catch { /* in use */ }
            }
            catch { }
        }
    }

    public void Activate() => FileGrid.Focus();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _opCts?.Cancel();
        StopEditing();
        SaveLocalPaneState();
        var client = _client;
        var shell = _shellClient;
        _client = null;
        _shellClient = null;
        _ = Task.Run(() =>
        {
            try { client?.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
            try { shell?.Dispose(); } catch { }
        });
        if (_ownsEndpoint) _endpoint.Dispose();
    }
}
