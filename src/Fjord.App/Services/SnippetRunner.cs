using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fjord.Core.Snippets;
using Fjord.Dialogs;
using Fjord.Views;

namespace Fjord.Services;

/// <summary>Sends snippets to a terminal, and builds the snippet menu (toolbar button and Ctrl+Shift+S).</summary>
public static class SnippetRunner
{
    public const int NumberedShortcuts = 9;   // Ctrl+Shift+1…9 run the first nine snippets

    public static void Run(TerminalTab tab, Snippet snippet)
    {
        if (tab.Client is null)
        {
            MessageBox.Show(Window.GetWindow(tab), "Connect first, then run the snippet.", snippet.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var owner = Window.GetWindow(tab);
        var expanded = SnippetStore.Expand(snippet.Command, label =>
        {
            var dlg = new InputDialog(snippet.Name, label + ":") { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Value : null;
        });
        if (expanded is null) { tab.Activate(); return; }   // a question was cancelled
        tab.Send(SnippetStore.ToKeystrokes(expanded, snippet.Run));
        tab.Activate();
    }

    /// <summary>Runs the n-th snippet (1-based), for Ctrl+Shift+1…9.</summary>
    public static void RunNumber(TerminalTab tab, int number)
    {
        var all = AppServices.Snippets.All;
        if (number >= 1 && number <= all.Count) Run(tab, all[number - 1]);
    }

    /// <param name="placementTarget">Open under this element (keyboard), or null to open at the mouse.</param>
    public static void ShowMenu(TerminalTab tab, UIElement? placementTarget)
    {
        var menu = new ContextMenu();
        if (placementTarget is not null)
        {
            menu.PlacementTarget = placementTarget;
            menu.Placement = PlacementMode.Bottom;
        }
        else menu.Placement = PlacementMode.MousePoint;

        var all = AppServices.Snippets.All;
        var folders = new Dictionary<string, MenuItem>(StringComparer.CurrentCultureIgnoreCase);
        for (int i = 0; i < all.Count; i++)
        {
            var snippet = all[i];
            var item = new MenuItem
            {
                Header = snippet.Name.Replace("_", "__"),   // "_" would become an access key
                ToolTip = snippet.Command,
                InputGestureText = i < NumberedShortcuts ? $"Ctrl+Shift+{i + 1}" : "",
            };
            item.Click += (_, _) => Run(tab, snippet);

            if (string.IsNullOrWhiteSpace(snippet.Folder)) { menu.Items.Add(item); continue; }
            if (!folders.TryGetValue(snippet.Folder.Trim(), out var folder))
            {
                folder = new MenuItem { Header = snippet.Folder.Trim().Replace("_", "__") };
                folders[snippet.Folder.Trim()] = folder;
                menu.Items.Add(folder);
            }
            folder.Items.Add(item);
        }

        if (all.Count == 0) menu.Items.Add(new MenuItem { Header = "No snippets yet", IsEnabled = false });
        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage snippets…" };
        manage.Click += (_, _) => SnippetsWindow.ShowFor(Window.GetWindow(tab));
        menu.Items.Add(manage);
        menu.Closed += (_, _) => tab.Activate();
        menu.IsOpen = true;
    }
}
