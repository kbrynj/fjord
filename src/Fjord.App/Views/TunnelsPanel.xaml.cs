using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Dialogs;
using Fjord.Services;

namespace Fjord.Views;

public enum TunnelState { Waiting, Stopped, Running, Failed }

/// <summary>One tunnel card in the panel (rule + live state).</summary>
public sealed class TunnelRow(TunnelRule rule, string server) : INotifyPropertyChanged
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xC6, 0x0C)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x3C)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xC2, 0x3C)));

    public TunnelRule Rule { get; private set; } = rule;
    public TunnelState State { get; private set; } = TunnelState.Waiting;
    public string? Error { get; private set; }
    public int Connections { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Set(TunnelState state, string? error = null)
    {
        State = state;
        Error = error;
        if (state != TunnelState.Running) Connections = 0;
        Notify();
    }

    public void Replace(TunnelRule rule) { Rule = rule; Notify(); }
    public void ConnectionOpened() { Connections++; if (State == TunnelState.Running) Error = null; Notify(); }
    public void ConnectionFailed(string message) { Error = message; Notify(); }

    public string StatusText => State switch
    {
        TunnelState.Running => Connections > 0 ? $"running · {Connections} used" : "running",
        TunnelState.Failed => "failed",
        TunnelState.Stopped => "stopped",
        _ => "starts when connected",
    };

    public Brush StatusBrush => State switch
    {
        TunnelState.Running => Error is null ? Green : Amber,
        TunnelState.Failed => Red,
        _ => Brushes.Gray,
    };

    /// <summary>Error if any; otherwise what to do with it (running) or what it does (not running).</summary>
    public string Message => Error ?? (State == TunnelState.Running ? Rule.UsageHint(server) : Rule.Describe(server));
    public Brush MessageBrush => Error is null ? Brushes.Gray : Red;

    public string ToggleLabel => State == TunnelState.Running ? "Stop" : "Start";
    public string? OpenLabel => Rule.Kind != TunnelKind.Local ? null : Rule.OpenAction switch
    {
        TunnelOpenAction.Http or TunnelOpenAction.Https => "Open in browser",
        TunnelOpenAction.RemoteDesktop => "Remote Desktop",
        _ => null,
    };
    public Visibility OpenVisibility => State == TunnelState.Running && OpenLabel is not null ? Visibility.Visible : Visibility.Collapsed;

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

/// <summary>
/// Side panel in a terminal tab: the session's tunnels, live status, one-click open — plus a built-in
/// explanation with ready-made examples, so a newcomer never has to leave the session to learn it.
/// </summary>
public partial class TunnelsPanel : UserControl
{
    private readonly ObservableCollection<TunnelRow> _rows = [];
    private SessionProfile _profile = new();
    private TunnelManager? _manager;
    private bool _helpPinned;

    /// <summary>Running count changed (for the toolbar badge).</summary>
    public event EventHandler? StateChanged;
    public event EventHandler? HideRequested;

    public int RunningCount => _rows.Count(r => r.State == TunnelState.Running);
    public int Count => _rows.Count;

    private string Server => string.IsNullOrWhiteSpace(_profile.Name) ? _profile.Host : _profile.DisplayName;

    public TunnelsPanel()
    {
        InitializeComponent();
        RowList.ItemsSource = _rows;
        QuickPresets.ItemsSource = TunnelPresets.All.Where(p => p.Id != "custom").ToList();
        _rows.CollectionChanged += (_, _) => UpdateHelp();
    }

    public void Initialize(SessionProfile profile)
    {
        _profile = profile;
        _rows.Clear();
        foreach (var rule in profile.Tunnels)
            _rows.Add(new TunnelRow(rule.Clone(), Server));
        NotSavedHint.Visibility = IsSavedProfile ? Visibility.Collapsed : Visibility.Visible;
        UpdateHelp();
    }

    private bool IsSavedProfile => AppServices.Profiles.Find(_profile.Id) is not null;

    // ───────────────────────── connection lifecycle ─────────────────────────

    /// <summary>
    /// Called by the terminal when it connects (manager) or disconnects (null).
    /// Starts auto-start tunnels; returns how many failed so the tab can show the panel.
    /// </summary>
    public int Attach(TunnelManager? manager)
    {
        if (_manager is not null)
        {
            _manager.ConnectionError -= OnConnectionError;
            _manager.ConnectionOpened -= OnConnectionOpened;
        }
        _manager = manager;

        int failures = 0;
        if (manager is null)
        {
            foreach (var row in _rows) row.Set(TunnelState.Waiting);
        }
        else
        {
            manager.ConnectionError += OnConnectionError;
            manager.ConnectionOpened += OnConnectionOpened;
            foreach (var row in _rows)
            {
                if (row.Rule.AutoStart) { if (!Start(row)) failures++; }
                else row.Set(TunnelState.Stopped);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return failures;
    }

    private bool Start(TunnelRow row)
    {
        if (_manager is null)
        {
            row.Set(TunnelState.Waiting);
            return false;
        }
        var error = _manager.Start(row.Rule);
        row.Set(error is null ? TunnelState.Running : TunnelState.Failed, error);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return error is null;
    }

    private void Stop(TunnelRow row)
    {
        _manager?.Stop(row.Rule.Id);
        row.Set(_manager is null ? TunnelState.Waiting : TunnelState.Stopped);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnConnectionError(Guid id, string message) =>
        Dispatcher.BeginInvoke(new Action(() => Find(id)?.ConnectionFailed(message)));

    private void OnConnectionOpened(Guid id) =>
        Dispatcher.BeginInvoke(new Action(() => Find(id)?.ConnectionOpened()));

    private TunnelRow? Find(Guid id) => _rows.FirstOrDefault(r => r.Rule.Id == id);

    // ───────────────────────── add / edit / remove ─────────────────────────

    public void AddTunnel(string? presetId = null)
    {
        var dlg = new TunnelDialog(null, Server, presetId, _rows.Select(r => r.Rule)) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result is not { } rule) return;

        var row = new TunnelRow(rule, Server);
        _rows.Add(row);
        Persist();
        if (_manager is not null) Start(row);   // start right away — that's what the user expects
    }

    private void Edit(TunnelRow row)
    {
        var dlg = new TunnelDialog(row.Rule, Server, otherRules: _rows.Select(r => r.Rule)) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result is not { } rule) return;

        bool wasRunning = row.State == TunnelState.Running;
        if (wasRunning) _manager?.Stop(row.Rule.Id);
        row.Replace(rule);
        Persist();
        if (_manager is not null && (wasRunning || rule.AutoStart)) Start(row);
        else row.Set(_manager is null ? TunnelState.Waiting : TunnelState.Stopped);
    }

    private void Remove(TunnelRow row)
    {
        if (MessageBox.Show(Window.GetWindow(this), $"Remove the tunnel '{row.Rule.DisplayName}'?", "Tunnels",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        _manager?.Stop(row.Rule.Id);
        _rows.Remove(row);
        Persist();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the rules into the stored session (never overwrites other edits to the session).</summary>
    private void Persist()
    {
        var rules = _rows.Select(r => r.Rule.Clone()).ToList();
        _profile.Tunnels = rules;
        if (AppServices.Profiles.Find(_profile.Id) is { } stored)
        {
            stored.Tunnels = rules.Select(r => r.Clone()).ToList();
            AppServices.Profiles.Upsert(stored);
        }
    }

    // ───────────────────────── UI handlers ─────────────────────────

    private static TunnelRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as TunnelRow;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.State == TunnelState.Running) Stop(row);
        else if (_manager is null) row.Set(TunnelState.Waiting, "Connect the session first — the tunnel starts then.");
        else Start(row);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var r = row.Rule;
        switch (r.OpenAction)
        {
            case TunnelOpenAction.Http or TunnelOpenAction.Https:
                AppServices.OpenInBrowser(r.OpenAddress!);
                break;
            case TunnelOpenAction.RemoteDesktop:
                Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:localhost:{r.ListenPort}") { UseShellExecute = true });
                break;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var r = row.Rule;
        var text = r.PresetId == "sqlserver" ? $"localhost,{r.ListenPort}" : r.OpenAddress ?? $"localhost:{r.ListenPort}";
        try { Clipboard.SetText(text); } catch { }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } row) Edit(row); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } row) Remove(row); }
    private void Add_Click(object sender, RoutedEventArgs e) => AddTunnel();

    private void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TunnelPreset preset) AddTunnel(preset.Id);
    }

    private void HelpToggle_Click(object sender, RoutedEventArgs e)
    {
        _helpPinned = !_helpPinned || _rows.Count == 0;
        UpdateHelp();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void Guide_Click(object sender, RoutedEventArgs e) =>
        GuideWindow.ShowSingle(Window.GetWindow(this), GuideWindow.TunnelsPage);

    private void UpdateHelp() =>
        HelpPanel.Visibility = _rows.Count == 0 || _helpPinned ? Visibility.Visible : Visibility.Collapsed;
}
