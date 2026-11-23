using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fjord.Services;

namespace Fjord.Views;

/// <summary>
/// Optional local pane (Settings → SFTP → Two panes). Upload: drag left → right, or the Upload buttons.
/// Download: drag right → left, or Download (which then goes straight to the left pane's folder).
/// Toggling the setting applies to open tabs immediately.
/// </summary>
public partial class SftpTab
{
    private bool _localNavigated;

    private bool TwoPane => Local.Visibility == Visibility.Visible;

    private void InitLocalPane()
    {
        Local.IsRemoteDragActive = () => s_activeDrag is not null;
        Local.StatusChanged += SetStatus;
        Local.UploadRequested += paths => _ = UploadAsync(paths);
        Local.RemoteDropRequested += folder => _ = DropIntoLocalAsync(folder);
        AppServices.Settings.Saved += OnSettingsSaved;
        ApplyPaneLayout(AppServices.Settings.SftpTwoPane);
    }

    private void OnSettingsSaved(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => { if (!_closed) ApplyPaneLayout(AppServices.Settings.SftpTwoPane); });

    private void ApplyPaneLayout(bool twoPane)
    {
        if (twoPane == TwoPane) return;
        Local.Visibility = PaneSplitter.Visibility = twoPane ? Visibility.Visible : Visibility.Collapsed;
        LocalColumn.Width = twoPane ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        LocalColumn.MinWidth = twoPane ? 200 : 0;
        SplitterColumn.Width = twoPane ? GridLength.Auto : new GridLength(0);

        if (twoPane && !_localNavigated)
        {
            _localNavigated = true;
            var start = AppServices.Settings.LastLocalFolder;
            Local.Navigate(start is not null && Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    /// <summary>Server files dragged onto the local pane (from this tab or another SFTP tab).</summary>
    private async Task DropIntoLocalAsync(string folder)
    {
        var drag = s_activeDrag;
        if (drag is null) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);   // let the drag finish first
        if (await drag.Source.DownloadToFolderAsync(drag.Entries, folder))
            Local.Refresh(select: drag.Entries.Count == 1 && folder == Local.CurrentFolder ? SafeFileName(drag.Entries[0].Name) : null);
    }

    /// <summary>Downloads into a local folder, asking first if that would overwrite anything.</summary>
    private async Task<bool> DownloadToFolderAsync(IReadOnlyList<RemoteEntry> items, string localDir)
    {
        var clashes = items.Select(i => SafeFileName(i.Name))
                           .Where(n => File.Exists(Path.Combine(localDir, n)) || Directory.Exists(Path.Combine(localDir, n)))
                           .ToList();
        if (clashes.Count > 0 && MessageBox.Show(Window.GetWindow(this),
                $"{clashes.Count} item(s) already exist in {localDir} and will be overwritten:\n\n{string.Join("\n", clashes.Take(10))}",
                "Overwrite?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;

        return await RunAsync("Preparing download…", async (client, ct) =>
        {
            var (count, bytes) = await Task.Run(() => DownloadTree(client, items, localDir, ct), ct);
            SetStatus($"Downloaded {count} file(s), {RemoteEntry.FormatSize(bytes)} → {localDir}");
        });
    }

    /// <summary>Remembers the local pane's folder for the next SFTP tab.</summary>
    private void SaveLocalPaneState()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        if (!_localNavigated || Local.CurrentFolder.Length == 0 || Local.CurrentFolder == AppServices.Settings.LastLocalFolder) return;
        AppServices.Settings.LastLocalFolder = Local.CurrentFolder;
        AppServices.Settings.Save();
    }
}
