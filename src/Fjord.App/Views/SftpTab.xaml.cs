using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fjord.Core.Profiles;
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
    public string Permissions { get; init; } = "";
    public int Owner { get; init; }

    public string Icon => IsDirectory ? "\uE8B7" : IsLink ? "\uE71B" : "\uE8A5";
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
/// SFTP browser. Can open on its own (new connection) or from a terminal tab, reusing that
/// session's credentials so you aren't asked for the password twice.
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
    public string Glyph => "\uE8B7";
    // The SFTP tab title never changes, so the event is never raised.
    public event EventHandler? TitleChanged { add { } remove { } }

    public SftpTab(MainWindow shell, SessionProfile profile, SshEndpoint? sharedEndpoint = null)
    {
        InitializeComponent();
        _shell = shell;
        Profile = profile;
        _ownsEndpoint = sharedEndpoint is null;
        _endpoint = sharedEndpoint ?? new SshEndpoint(profile, new UiPrompts(shell), AppServices.KnownHosts, AppServices.Protector);

        NavButtons.Children.Add(UiHelpers.ToolbarButton("\uE74A", null, "Up one folder (Backspace)", (_, _) => _ = NavigateAsync(ParentPath(_path))));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("\uE80F", null, "Home folder", (_, _) => _ = NavigateAsync(_client?.WorkingDirectory ?? "/")));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("\uE72C", null, "Refresh (F5)", (_, _) => _ = NavigateAsync(_path)));

        ActionButtons.Children.Add(UiHelpers.ToolbarButton("\uE8F4", "New folder", null, NewFolder_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("\uE898", "Upload", "Upload files (or drag them in from Explorer)", Upload_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("\uE896", "Download", "Download selected", Download_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("\uE74D", null, "Delete selected (Del)", Delete_Click));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("\uE756", null, "Open a terminal to this server", (_, _) => _shell.OpenTerminal(Profile)));

        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        SetStatus($"Connecting to {Profile.Target}…");
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

    private async Task NavigateAsync(string path)
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
            SetStatus($"{entries.Count(x => x.IsDirectory)} folders, {entries.Count(x => !x.IsDirectory)} files");
        });
    }

    private static RemoteEntry ToEntry(ISftpFile f) => new()
    {
        Name = f.Name,
        FullName = f.FullName,
        IsDirectory = f.IsDirectory,
        IsLink = f.IsSymbolicLink,
        Size = f.Length,
        Modified = f.LastWriteTime,
        Owner = f.UserId,
        Permissions = (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-") +
                      (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") +
                      (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") +
                      (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-"),
    };

    private static string Combine(string dir, string name) => dir.TrimEnd('/') + "/" + name;

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    private List<RemoteEntry> Selected => FileGrid.SelectedItems.Cast<RemoteEntry>().ToList();

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
            case Key.F5: _ = NavigateAsync(_path); e.Handled = true; break;
            case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is not null) Open_Click(sender, e);
    }

    /// <summary>Folders: navigate. Files: download to a temp folder and open with the default Windows app.</summary>
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not RemoteEntry entry) return;
        if (entry.IsDirectory || entry.IsLink && await IsDirectoryAsync(entry.FullName))
        {
            await NavigateAsync(entry.FullName);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "Fjord", SafeFileName(Profile.Host));
        Directory.CreateDirectory(tempDir);
        var local = Path.Combine(tempDir, SafeFileName(entry.Name));
        await RunAsync($"Opening {entry.Name}…", async (client, ct) =>
        {
            await DownloadFileAsync(client, entry.FullName, local, entry.Size, null, ct);
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
            SetStatus($"Opened a local copy of {entry.Name} (changes are not uploaded automatically).");
        });
    }

    private async Task<bool> IsDirectoryAsync(string path)
    {
        if (_client is null) return false;
        try { return await Task.Run(() => _client.Get(path).IsDirectory); }
        catch { return false; }
    }

    // ───────────────────────── file operations ─────────────────────────

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = $"Upload to {_path}", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            _ = UploadAsync(dlg.FileNames);
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            _ = UploadAsync(paths);
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_client is null) return;
        var existing = (FileGrid.ItemsSource as IEnumerable<RemoteEntry>)?.Select(x => x.Name).ToHashSet() ?? [];
        var clashes = localPaths.Select(Path.GetFileName).Where(n => n is not null && existing.Contains(n)).ToList();
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
        await NavigateAsync(_path);
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

        await RunAsync("Preparing download…", async (client, ct) =>
        {
            var files = await Task.Run(() =>
            {
                var list = new List<(string Remote, string Local, long Size)>();
                foreach (var item in items) CollectDownloads(client, item.FullName, item.IsDirectory, target, list, ct);
                return list;
            }, ct);

            long total = Math.Max(1, files.Sum(f => f.Size)), done = 0;
            foreach (var (remote, local, size) in files)
            {
                long before = done;
                await DownloadFileAsync(client, remote, local, size, b => ReportProgress((before + b) * 100.0 / total, $"Downloading {Path.GetFileName(local)}"), ct);
                done += size;
            }
            SetStatus($"Downloaded {files.Count} file(s), {RemoteEntry.FormatSize(done)} → {target}");
        });
    }

    private static void CollectDownloads(SftpClient client, string remote, bool isDir, string localDir, List<(string, string, long)> list, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var local = Path.Combine(localDir, SafeFileName(remote.TrimEnd('/').Split('/').Last()));
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

    private static Task DownloadFileAsync(SftpClient client, string remote, string local, long size, Action<long>? progress, CancellationToken ct) =>
        Task.Run(() =>
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
        }, ct);

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("New folder", $"Folder name in {_path}:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        var path = Combine(_path, dlg.Value.Trim());
        await RunAsync("Creating folder…", (client, ct) => Task.Run(() => client.CreateDirectory(path), ct));
        await NavigateAsync(_path);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not RemoteEntry entry) return;
        var dlg = new InputDialog("Rename", $"New name for {entry.Name}:", initialValue: entry.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value) || dlg.Value == entry.Name) return;
        var target = Combine(_path, dlg.Value.Trim());
        await RunAsync("Renaming…", (client, ct) => Task.Run(() => client.RenameFile(entry.FullName, target), ct));
        await NavigateAsync(_path);
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
            foreach (var item in items) DeleteRecursive(client, item.FullName, item.IsDirectory, ct);
        }, ct));
        await NavigateAsync(_path);
    }

    private static void DeleteRecursive(SftpClient client, string path, bool isDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isDir) { client.DeleteFile(path); return; }
        foreach (var f in client.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(client, f.FullName, f.IsDirectory, ct);   // symlinks to dirs report IsDirectory=false → unlinked, not followed
        client.DeleteDirectory(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var text = FileGrid.SelectedItem is RemoteEntry entry ? entry.FullName : _path;
        try { Clipboard.SetText(text); } catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _opCts?.Cancel();

    // ───────────────────────── plumbing ─────────────────────────

    /// <summary>Runs one SFTP operation at a time with busy UI, cancel button and error reporting.</summary>
    private async Task RunAsync(string description, Func<SftpClient, CancellationToken, Task> op)
    {
        var client = _client;
        if (client is null || _closed) return;
        if (!await _gate.WaitAsync(0))
        {
            SetStatus("Busy — wait for the current operation to finish.");
            return;
        }

        _opCts = new CancellationTokenSource();
        SetStatus(description);
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        try
        {
            await op(client, _opCts.Token);
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

    public void Activate() => FileGrid.Focus();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _opCts?.Cancel();
        var client = _client;
        _client = null;
        _ = Task.Run(() =>
        {
            try { client?.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
        });
        if (_ownsEndpoint) _endpoint.Dispose();
    }
}
