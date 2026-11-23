using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Fjord.Core.Sftp;
using Renci.SshNet;

namespace Fjord.Views;

/// <summary>
/// Drag and drop:
/// • Explorer → list: upload.
/// • List → Explorer / desktop: download, as "virtual files" (<see cref="VirtualFileDataObject"/>) — Explorer
///   pulls the contents after the drop, on its own thread, with its own progress dialog.
/// • List → a folder in the list: move (hold Ctrl to copy).
/// • List → another SFTP tab: copy between servers (download to temp, then upload).
/// </summary>
public partial class SftpTab
{
    /// <summary>The drag currently running in this app (drags are modal, so there's at most one).</summary>
    private sealed record ActiveDrag(SftpTab Source, IReadOnlyList<RemoteEntry> Entries);
    private static ActiveDrag? s_activeDrag;

    private Point? _dragStart;
    private RemoteEntry? _selectOnMouseUp;

    // ───────────── starting a drag ─────────────

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _selectOnMouseUp = null;
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(FileGrid, d) is not DataGridRow row) return;
        _dragStart = e.GetPosition(FileGrid);

        // Pressing on a row of a multi-selection would normally reduce the selection to that row,
        // which makes it impossible to drag several files. Keep the selection until the button is released.
        if (row.IsSelected && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && FileGrid.SelectedItems.Count > 1)
        {
            _selectOnMouseUp = row.Item as RemoteEntry;
            FileGrid.Focus();
            e.Handled = true;
        }
    }

    private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnMouseUp is { } item) FileGrid.SelectedItem = item;   // it was a click, not a drag
        _selectOnMouseUp = null;
        _dragStart = null;
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || s_activeDrag is not null) return;
        var delta = e.GetPosition(FileGrid) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStart = null;
        _selectOnMouseUp = null;
        var items = Selected;
        var client = _client;
        if (items.Count == 0 || client is null) return;

        var data = new VirtualFileDataObject(() => ListForExplorer(client, items));
        int copied = 0;
        data.FileRequested += f => { copied++; SetStatus($"Copying to Windows: {f.RelativePath}"); };
        data.OperationEnded += ok => SetStatus(ok ? $"Copied {copied} file(s) to Windows." : "Copy to Windows was cancelled or failed.");
        data.Failed += ex => SetStatus("✖ " + ex.Message);

        s_activeDrag = new ActiveDrag(this, items);
        try
        {
            OleDrag.Run(data, OleDrag.DROPEFFECT_COPY | OleDrag.DROPEFFECT_MOVE);
        }
        catch (Exception ex)
        {
            SetStatus("✖ Drag failed: " + ex.Message);
        }
        finally
        {
            s_activeDrag = null;
        }
    }

    /// <summary>
    /// The files Explorer will create: folders are walked so their contents come along.
    /// Runs when Explorer asks for the list (at the drop). Contents are read later, file by file.
    /// </summary>
    private static List<VirtualFile> ListForExplorer(SftpClient client, IEnumerable<RemoteEntry> items)
    {
        var list = new List<VirtualFile>();

        void Add(string remote, string relative, bool isDir, bool isLink)
        {
            var attrs = isLink ? client.GetAttributes(remote) : null;   // follow symlinks to see what they point at
            if (attrs is not null) isDir = attrs.IsDirectory;

            if (isDir)
            {
                list.Add(new VirtualFile(relative, true, 0, DateTime.UtcNow, null));
                foreach (var f in client.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
                    Add(f.FullName, relative + "\\" + SafeFileName(f.Name), f.IsDirectory, f.IsSymbolicLink);
            }
            else
            {
                var a = attrs ?? client.GetAttributes(remote);
                list.Add(new VirtualFile(relative, false, a.Size, a.LastWriteTimeUtc, () => client.OpenRead(remote)));
            }
        }

        foreach (var item in items)
            Add(item.FullName, SafeFileName(item.Name), item.IsDirectory, item.IsLink);
        return list;
    }

    // ───────────── dropping onto the list ─────────────

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropEffectFor(e, out _);
        e.Handled = true;
    }

    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        var effect = DropEffectFor(e, out var targetFolder);
        if (effect == DragDropEffects.None) return;
        e.Handled = true;

        var drag = s_activeDrag;
        // Read what's needed now; everything else happens after the drag has finished.
        var paths = drag is null ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (drag is not null && drag.Source == this)
            await TransferAsync(drag.Entries, targetFolder!, move: effect == DragDropEffects.Move);
        else if (drag is not null)
            await CopyFromOtherTabAsync(drag);
        else if (paths is { Length: > 0 })
            await UploadAsync(paths);
    }

    private DragDropEffects DropEffectFor(DragEventArgs e, out string? targetFolder)
    {
        targetFolder = null;
        if (_client is null) return DragDropEffects.None;

        if (s_activeDrag is { } drag)
        {
            if (drag.Source != this) return DragDropEffects.Copy;   // another tab → copy here

            // Within this list: only onto a folder that isn't part of what's being dragged.
            if (e.OriginalSource is DependencyObject d &&
                ItemsControl.ContainerFromElement(FileGrid, d) is DataGridRow { Item: RemoteEntry { IsDirectory: true } folder } &&
                !drag.Entries.Any(x => x.FullName == folder.FullName))
            {
                targetFolder = folder.FullName;
                return (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            return DragDropEffects.None;
        }

        return e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Files dragged from another SFTP tab (possibly another server): download there, upload here.</summary>
    private async Task CopyFromOtherTabAsync(ActiveDrag drag)
    {
        var temp = Path.Combine(TempRoot("drag"), Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var source = drag.Source;
        var ok = await source.RunAsync("Downloading for the other tab…",
            (client, ct) => Task.Run(() => source.DownloadTree(client, drag.Entries, temp, ct), ct));
        if (!ok)
        {
            SetStatus("✖ Couldn't download from the other tab: " + source.StatusText.Text);
            return;
        }
        await UploadAsync(Directory.EnumerateFileSystemEntries(temp).ToList());
        try { Directory.Delete(temp, recursive: true); } catch { }
    }
}
