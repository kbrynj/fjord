using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Fjord.Core.Snippets;
using Fjord.Services;

namespace Fjord.Views;

/// <summary>Editable copy of a snippet for the manager window (notifies the list when the name changes).</summary>
public sealed class SnippetItem : INotifyPropertyChanged
{
    private string _name = "", _folder = "", _command = "";
    private bool _run = true;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public string Command { get => _command; set => Set(ref _command, value); }
    public bool Run { get => _run; set => Set(ref _run, value); }
    /// <summary>Shown under the name: folder and first line of the command.</summary>
    public string Subtitle => (string.IsNullOrWhiteSpace(Folder) ? "" : Folder.Trim() + " · ") + Command.Split('\n')[0].Trim();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Edited;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        Edited?.Invoke(this, EventArgs.Empty);
    }

    public static SnippetItem From(Snippet s) => new() { Id = s.Id, Name = s.Name, Folder = s.Folder, Command = s.Command, Run = s.Run };
    public Snippet ToSnippet() => new() { Id = Id, Name = Name.Trim(), Folder = Folder.Trim(), Command = Command, Run = Run };
}

/// <summary>Create, edit, reorder and delete snippets. Non-modal (one at a time); changes apply on Save.</summary>
public partial class SnippetsWindow : Window
{
    private static SnippetsWindow? s_open;

    private readonly ObservableCollection<SnippetItem> _items = [];
    private bool _dirty;

    public static void ShowFor(Window? owner)
    {
        if (s_open is { } open) { open.Activate(); return; }
        s_open = new SnippetsWindow { Owner = owner };
        s_open.Closed += (_, _) => s_open = null;
        s_open.Show();
    }

    private SnippetsWindow()
    {
        InitializeComponent();
        foreach (var s in AppServices.Snippets.All) Track(SnippetItem.From(s));
        List.ItemsSource = _items;

        ListButtons.Children.Add(UiHelpers.ToolbarButton("", "Add", "New snippet", (_, _) => Add(new SnippetItem { Name = "New snippet", Command = "" })));
        ListButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Duplicate", (_, _) => Duplicate()));
        ListButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Move up (the first nine get Ctrl+Shift+1…9)", (_, _) => Move(-1)));
        ListButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Move down", (_, _) => Move(+1)));
        ListButtons.Children.Add(UiHelpers.ToolbarButton("", null, "Delete", (_, _) => Delete()));

        List.SelectedIndex = _items.Count > 0 ? 0 : -1;
        UpdateEditor();
        _dirty = false;
    }

    private void Track(SnippetItem item)
    {
        item.Edited += (_, _) => _dirty = true;
        _items.Add(item);
    }

    private SnippetItem? Selected => List.SelectedItem as SnippetItem;

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateEditor();

    private void UpdateEditor()
    {
        Editor.DataContext = Selected;
        Editor.Visibility = Selected is null ? Visibility.Hidden : Visibility.Visible;
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderBox.ItemsSource = _items.Select(i => i.Folder.Trim()).Where(f => f.Length > 0).Distinct().Order().ToList();
    }

    private void Add(SnippetItem item)
    {
        var at = Selected is { } sel ? _items.IndexOf(sel) + 1 : _items.Count;
        item.Edited += (_, _) => _dirty = true;
        _items.Insert(at, item);
        _dirty = true;
        List.SelectedItem = item;
        List.ScrollIntoView(item);
    }

    private void Duplicate()
    {
        if (Selected is not { } s) return;
        Add(new SnippetItem { Name = s.Name + " (copy)", Folder = s.Folder, Command = s.Command, Run = s.Run });
    }

    private void Move(int delta)
    {
        if (Selected is not { } s) return;
        int from = _items.IndexOf(s), to = from + delta;
        if (to < 0 || to >= _items.Count) return;
        _items.Move(from, to);
        _dirty = true;
        List.SelectedItem = s;
    }

    private void Delete()
    {
        if (Selected is not { } s) return;
        if (MessageBox.Show(this, $"Delete the snippet '{s.Name}'?", "Snippets", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int index = _items.IndexOf(s);
        _items.Remove(s);
        _dirty = true;
        List.SelectedIndex = Math.Min(index, _items.Count - 1);
        UpdateEditor();
    }

    private bool Save()
    {
        var unnamed = _items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.Command));
        if (unnamed is not null)
        {
            List.SelectedItem = unnamed;
            MessageBox.Show(this, "Every snippet needs a name and a command.", "Snippets", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        AppServices.Snippets.ReplaceAll(_items.Select(i => i.ToSnippet()));
        _dirty = false;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "Save your changes to the snippets?", "Snippets", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
