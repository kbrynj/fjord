using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fjord.Dialogs;
using Microsoft.VisualBasic.FileIO;

namespace Fjord.Views;

/// <summary>One row in the local file list: a drive, folder or file on this PC.</summary>
public sealed class LocalEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }

    public string Icon => IsDrive ? "" : IsDirectory ? "" : "";
    public Brush IconBrush => IsDirectory && !IsDrive ? FolderBrush : Brushes.Gray;
    public string SizeText => IsDirectory ? "" : RemoteEntry.FormatSize(Size);
    public string SortKey => (IsDirectory ? "0" : "1") + Name.ToLowerInvariant();

    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39));
}

/// <summary>
/// The "this PC" side of a two-pane SFTP tab. Knows nothing about SSH: it asks its host (SftpTab) to
/// upload, and the host tells it about server files dropped onto it.
/// </summary>
public partial class LocalPane : UserControl
{
    /// <summary>"" = This PC (the list of drives).</summary>
    public string CurrentFolder { get; private set; } = "";

    /// <summary>Upload these local files/folders to the server's current folder.</summary>
    public event Action<IReadOnlyList<string>>? UploadRequested;
    /// <summary>Server files were dropped here; download them into this folder.</summary>
    public event Action<string>? RemoteDropRequested;
    /// <summary>Status text for the tab's status bar.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Set by the host: true while files from a server list are being dragged.</summary>
    public Func<bool> IsRemoteDragActive { get; set; } = () => false;

    private Point? _dragStart;
    private LocalEntry? _selectOnMouseUp;
    private bool _dragging;

    public LocalPane()
    {
        InitializeComponent();
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Up one folder (Backspace)", (_, _) => Up()));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Your user folder",
            (_, _) => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "This PC (drives)", (_, _) => Navigate("")));
        NavButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Refresh (F5)", (_, _) => Refresh()));
        ActionButtons.Children.Add(UiHelpers.ToolbarButton("", "Upload", "Upload the selected files to the server's folder (or drag them across)", Upload_Click));
    }

    public IReadOnlyList<LocalEntry> Selected => FileGrid.SelectedItems.Cast<LocalEntry>().Where(x => !x.IsDrive).ToList();
    private IEnumerable<LocalEntry> Listed => FileGrid.ItemsSource as IEnumerable<LocalEntry> ?? [];

    // ───────────── navigation ─────────────

    public void Navigate(string path, string? select = null)
    {
        try
        {
            List<LocalEntry> entries;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "";
                entries = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new LocalEntry
                {
                    Name = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})",
                    FullPath = d.RootDirectory.FullName,
                    IsDirectory = true,
                    IsDrive = true,
                }).ToList();
            }
            else
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                var dir = new DirectoryInfo(path);
                if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");
                entries = dir.EnumerateFileSystemInfos()
                    .Where(i => (i.Attributes & FileAttributes.Hidden) == 0)   // like Explorer's default
                    .Select(i => new LocalEntry
                    {
                        Name = i.Name,
                        FullPath = i.FullName,
                        IsDirectory = i is DirectoryInfo,
                        Size = i is FileInfo f ? f.Length : 0,
                        Modified = i.LastWriteTime,
                    })
                    .OrderBy(x => x.SortKey, StringComparer.Ordinal)
                    .ToList();
            }

            CurrentFolder = path;
            PathBox.Text = path.Length == 0 ? "This PC" : path;
            FileGrid.ItemsSource = entries;
            if (select is not null && entries.FirstOrDefault(x => string.Equals(x.Name, select, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                FileGrid.SelectedItem = item;
                FileGrid.ScrollIntoView(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusChanged?.Invoke("✖ " + ex.Message);
            PathBox.Text = CurrentFolder.Length == 0 ? "This PC" : CurrentFolder;
        }
    }

    public void Refresh(string? select = null) => Navigate(CurrentFolder, select);

    private void Up()
    {
        if (CurrentFolder.Length == 0) return;
        var parent = Directory.GetParent(CurrentFolder.TrimEnd('\\'))?.FullName;
        Navigate(parent ?? "", select: Path.GetFileName(CurrentFolder.TrimEnd('\\')) is { Length: > 0 } n ? n : null);
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate(PathBox.Text == "This PC" ? "" : PathBox.Text);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Open_Click(sender, e); e.Handled = true; break;
            case Key.Back: Up(); e.Handled = true; break;
            case Key.F5: Refresh(); e.Handled = true; break;
            case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileGrid, d) is DataGridRow)
            Open_Click(sender, e);
    }

    // ───────────── commands ─────────────

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not LocalEntry entry) return;
        if (entry.IsDirectory) { Navigate(entry.FullPath); return; }
        try { Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { StatusChanged?.Invoke("✖ " + ex.Message); }
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var paths = Selected.Select(x => x.FullPath).ToList();
        if (paths.Count == 0) { StatusChanged?.Invoke("Select files or folders on the left to upload."); return; }
        UploadRequested?.Invoke(paths);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not LocalEntry { IsDrive: false } entry) return;
        var dlg = new InputDialog("Rename", $"New name for {entry.Name}:", initialValue: entry.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value) || dlg.Value.Trim() == entry.Name) return;
        var name = dlg.Value.Trim();
        try
        {
            var target = Path.Combine(CurrentFolder, name);
            if (entry.IsDirectory) Directory.Move(entry.FullPath, target);
            else File.Move(entry.FullPath, target);
            Refresh(select: name);
        }
        catch (Exception ex) { StatusChanged?.Invoke("✖ " + ex.Message); }
    }

    /// <summary>Deletes to the Recycle Bin, so mistakes can be undone.</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        if (MessageBox.Show(Window.GetWindow(this), $"Move {what} to the Recycle Bin?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var item in items)
            {
                if (item.IsDirectory) FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke("✖ " + ex.Message); }
        Refresh();
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFolder.Length == 0) return;
        var dlg = new InputDialog("New folder", $"Folder name in {CurrentFolder}:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(CurrentFolder, dlg.Value.Trim()));
            Refresh(select: dlg.Value.Trim());
        }
        catch (Exception ex) { StatusChanged?.Invoke("✖ " + ex.Message); }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var args = FileGrid.SelectedItem is LocalEntry { IsDrive: false } entry ? $"/select,\"{entry.FullPath}\""
                 : CurrentFolder.Length > 0 ? $"\"{CurrentFolder}\"" : "";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    // ───────────── drag out (to the server pane, or anywhere that takes files) ─────────────

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _selectOnMouseUp = null;
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(FileGrid, d) is not DataGridRow row) return;
        _dragStart = e.GetPosition(FileGrid);
        if (row.IsSelected && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && FileGrid.SelectedItems.Count > 1)
        {
            _selectOnMouseUp = row.Item as LocalEntry;
            FileGrid.Focus();
            e.Handled = true;
        }
    }

    private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnMouseUp is { } item) FileGrid.SelectedItem = item;
        _selectOnMouseUp = null;
        _dragStart = null;
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        var delta = e.GetPosition(FileGrid) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStart = null;
        _selectOnMouseUp = null;
        var paths = Selected.Select(x => x.FullPath).ToArray();
        if (paths.Length == 0) return;

        _dragging = true;
        try { DragDrop.DoDragDrop(FileGrid, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy); }
        finally { _dragging = false; }
    }

    // ───────────── drop in (files from the server pane) ─────────────

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TargetFor(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (TargetFor(e) is not { } folder) return;
        e.Handled = true;
        RemoteDropRequested?.Invoke(folder);
    }

    /// <summary>Server files can be dropped into the current folder, or onto a folder/drive row.</summary>
    private string? TargetFor(DragEventArgs e)
    {
        if (_dragging || !IsRemoteDragActive()) return null;
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileGrid, d) is DataGridRow { Item: LocalEntry { IsDirectory: true } row })
            return row.FullPath;
        return CurrentFolder.Length > 0 ? CurrentFolder : null;
    }
}
