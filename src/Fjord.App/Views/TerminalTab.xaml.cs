using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Features;
using Fjord.Services;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet;

namespace Fjord.Views;

/// <summary>
/// One SSH terminal: SSH.NET shell channel ⇄ xterm.js running in WebView2 (Assets/terminal).
/// Output is batched per dispatcher tick; input/resizes/clipboard come back as JSON messages.
/// </summary>
public partial class TerminalTab : UserControl, ISessionTab
{
    private enum State { Starting, Connecting, Connected, Disconnected }

    private readonly MainWindow _shell;
    private readonly TaskCompletionSource _pageReady = new();
    private CancellationTokenSource? _connectCts;
    private SshEndpoint? _endpoint;
    private SshClient? _client;
    private ShellStream? _stream;
    private TunnelManager? _tunnelManager;
    private readonly Dictionary<IAppFeature, Button> _featureButtons = [];
    private State _state = State.Starting;
    private uint _cols = 80, _rows = 24;
    private bool _closed;

    // Output batching: background reader appends, UI thread flushes to the page.
    private readonly object _outLock = new();
    private readonly MemoryStream _pending = new();
    private bool _flushScheduled;

    public SessionProfile Profile { get; }
    public string Title { get; private set; }
    public string Glyph => "\uE756";
    public event EventHandler? TitleChanged;
    public event EventHandler<string>? HotkeyPressed;

    /// <summary>The connected client (null when disconnected). Features use it to run commands.</summary>
    public SshClient? Client => _state == State.Connected ? _client : null;

    /// <summary>Authenticated endpoint — open SFTP or more channels without asking for the password again.</summary>
    public SshEndpoint? Endpoint => _endpoint;

    public TerminalTab(MainWindow shell, SessionProfile profile)
    {
        InitializeComponent();
        _shell = shell;
        Profile = profile;
        Title = profile.DisplayName;
        View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 12, 12);

        foreach (var feature in FeatureRegistry.SessionFeatures)
        {
            var button = UiHelpers.ToolbarButton(feature.Glyph, feature.Title, feature.ToolTip,
                (_, _) => feature.Execute(new FeatureContext(_shell, this)));
            _featureButtons[feature] = button;
            ActionsPanel.Children.Add(button);
        }

        Tunnels.Initialize(profile);
        Tunnels.HideRequested += (_, _) => SetTunnelsVisible(false);
        Tunnels.StateChanged += (_, _) => UpdateTunnelsButton();
        UpdateTunnelsButton();
        ActionsPanel.Children.Add(UiHelpers.ToolbarButton("\uE72C", null, "Reconnect", (_, _) => _ = ReconnectAsync()));
        ActionsPanel.Children.Add(UiHelpers.ToolbarButton("\uE7E8", null, "Disconnect", (_, _) => Disconnect("Disconnected.")));

        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        try
        {
            await AppServices.InitWebViewAsync(View);
            View.CoreWebView2.WebMessageReceived += OnWebMessage;

            var s = AppServices.Settings;
            var cfg = JsonSerializer.Serialize(new
            {
                fontFamily = s.FontFamily, fontSize = s.FontSize, scrollback = s.Scrollback,
                copyOnSelect = s.CopyOnSelect, cursorBlink = s.CursorBlink,
            });
            var cfgParam = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg)));
            View.CoreWebView2.Navigate($"https://{AppServices.AssetsHost}/terminal/terminal.html?cfg={cfgParam}");

            await _pageReady.Task;
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            SetState(State.Disconnected, "Terminal failed to start");
            UiHelpers.ShowError(this, "Could not start the terminal view (is the WebView2 runtime installed?)\n\n" + ex.Message);
        }
    }

    // ───────────────────────── connection ─────────────────────────

    private async Task ConnectAsync()
    {
        if (_closed) return;
        SetState(State.Connecting, $"Connecting to {Profile.Target}…");
        PostStatus($"Connecting to {Profile.Target}…");

        _connectCts = new CancellationTokenSource();
        try
        {
            _endpoint ??= new SshEndpoint(Profile, new UiPrompts(_shell), AppServices.KnownHosts, AppServices.Protector);
            var client = await _endpoint.ConnectSshAsync(_connectCts.Token);
            if (_closed) { client.Dispose(); return; }

            SaveProfileChanges();
            _client = client;
            _stream = client.CreateShellStream("xterm-256color", _cols, _rows, 0, 0, 64 * 1024);
            StartReader(_stream);

            if (!string.IsNullOrWhiteSpace(Profile.StartupCommand))
                Send(Profile.StartupCommand + "\r");

            SetState(State.Connected, $"{Profile.Target}  ·  {Profile.Host}");

            // Tunnels: start the auto-start ones; only show the panel if something needs attention.
            _tunnelManager = new TunnelManager(client, Profile.DisplayName);
            if (Tunnels.Attach(_tunnelManager) > 0) SetTunnelsVisible(true);
            Activate();
        }
        catch (OperationCanceledException)
        {
            PostStatus("Cancelled. Press Enter to try again.");
            SetState(State.Disconnected, "Not connected");
        }
        catch (Exception ex)
        {
            PostStatus("✖ " + ex.Message);
            PostStatus("Press Enter to reconnect.");
            SetState(State.Disconnected, "Not connected");
        }
    }

    private void SaveProfileChanges()
    {
        if (AppServices.Profiles.Find(Profile.Id) is null) return;   // quick-connect, not a saved session
        if (_endpoint?.ProfileModified == true) AppServices.Profiles.Upsert(Profile);
        AppServices.Profiles.Touch(Profile.Id);
    }

    private void StartReader(ShellStream stream)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    EnqueueOutput(buffer.AsSpan(0, n));
            }
            catch (Exception) { /* stream torn down */ }

            Dispatcher.BeginInvoke(new Action(() => OnStreamEnded(stream)));
        })
        { IsBackground = true, Name = "Fjord terminal reader" };
        thread.Start();
    }

    private void OnStreamEnded(ShellStream stream)
    {
        if (!ReferenceEquals(stream, _stream) || _closed) return;   // a reconnect already replaced it
        FlushOutput();
        Disconnect("Session closed. Press Enter to reconnect.");
    }

    private void Disconnect(string message)
    {
        _connectCts?.Cancel();
        var client = _client;
        var stream = _stream;
        var tunnels = _tunnelManager;
        _client = null;
        _stream = null;
        _tunnelManager = null;
        Tunnels.Attach(null);
        if (_state != State.Disconnected) PostStatus(message);
        SetState(State.Disconnected, "Not connected");

        // Disconnecting can block on a dead network — keep it off the UI thread.
        _ = Task.Run(() =>
        {
            try { tunnels?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client?.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
        });
    }

    public async Task ReconnectAsync()
    {
        if (_state == State.Connecting) return;
        if (_state == State.Connected) Disconnect("Reconnecting…");
        await ConnectAsync();
    }

    // ───────────────────────── terminal I/O ─────────────────────────

    private void EnqueueOutput(ReadOnlySpan<byte> data)
    {
        lock (_outLock)
        {
            _pending.Write(data);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(FlushOutput));
    }

    private void FlushOutput()
    {
        byte[] chunk;
        lock (_outLock)
        {
            chunk = _pending.ToArray();
            _pending.SetLength(0);
            _flushScheduled = false;
        }

        const int maxMessage = 1 << 20;
        for (int offset = 0; offset < chunk.Length; offset += maxMessage)
            Post(new { t = "data", b = Convert.ToBase64String(chunk, offset, Math.Min(maxMessage, chunk.Length - offset)) });
    }

    /// <summary>Sends keystrokes/text to the remote shell.</summary>
    public void Send(string text) => SendBytes(Encoding.UTF8.GetBytes(text));

    private void SendBytes(byte[] bytes)
    {
        var stream = _stream;
        if (stream is null || _state != State.Connected) return;
        try
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            Disconnect("✖ " + ex.Message);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json;
        try { json = e.TryGetWebMessageAsString(); } catch { return; }
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                ReadSize(m);
                _pageReady.TrySetResult();
                break;

            case "resize":
                ReadSize(m);
                try { _stream?.ChangeWindowSize(_cols, _rows, 0, 0); } catch { }
                break;

            case "input":
                var input = m.GetProperty("d").GetString() ?? "";
                if (_state == State.Disconnected && input == "\r") _ = ReconnectAsync();
                else Send(input);
                break;

            case "binary":   // mouse reports etc. — one char per byte
                SendBytes(Encoding.Latin1.GetBytes(m.GetProperty("d").GetString() ?? ""));
                break;

            case "copy":
                TrySetClipboard(m.GetProperty("text").GetString());
                break;

            case "paste":
                Paste();
                break;

            case "title":
                StatusText.Text = m.GetProperty("title").GetString();
                break;

            case "open":
                AppServices.OpenInBrowser(m.GetProperty("uri").GetString() ?? "");
                break;

            case "hotkey":
                HotkeyPressed?.Invoke(this, m.GetProperty("k").GetString() ?? "");
                break;
        }
    }

    private void ReadSize(JsonElement m)
    {
        _cols = (uint)Math.Max(1, m.GetProperty("cols").GetInt32());
        _rows = (uint)Math.Max(1, m.GetProperty("rows").GetInt32());
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrEmpty(text) || _state != State.Connected) return;
        // Hand it to xterm.js: it converts newlines to CR and applies bracketed-paste when the shell wants it.
        View.CoreWebView2?.ExecuteScriptAsync($"term.paste({JsonSerializer.Serialize(text)})");
    }

    private static void TrySetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        for (int i = 0; i < 5; i++)   // clipboard can be briefly locked by other apps
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(20); }
        }
    }

    private void Post(object message)
    {
        try { View.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message)); }
        catch (InvalidOperationException) { /* WebView already disposed */ }
    }

    private void PostStatus(string text) => Post(new { t = "status", text });

    // ───────────────────────── tab plumbing ─────────────────────────

    private void SetState(State state, string status)
    {
        _state = state;
        StatusText.Text = status;
        StatusDot.Fill = state switch
        {
            State.Connected => new SolidColorBrush(Color.FromRgb(0x16, 0xC6, 0x0C)),
            State.Connecting => new SolidColorBrush(Color.FromRgb(0xF9, 0xC2, 0x3C)),
            _ => Brushes.Gray,
        };
        var title = Profile.DisplayName + (state == State.Disconnected ? " (offline)" : "");
        if (title != Title)
        {
            Title = title;
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ───────────────────────── tunnels panel ─────────────────────────

    public bool TunnelsVisible => Tunnels.Visibility == Visibility.Visible;

    public void SetTunnelsVisible(bool visible) =>
        Tunnels.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void ToggleTunnels() => SetTunnelsVisible(!TunnelsVisible);

    /// <summary>Shows the running count on the toolbar button, e.g. "Tunnels · 2".</summary>
    private void UpdateTunnelsButton()
    {
        var entry = _featureButtons.FirstOrDefault(kv => kv.Key is TunnelsFeature);
        if (entry.Value?.Content is StackPanel { Children.Count: > 1 } content && content.Children[1] is TextBlock label)
        {
            var running = Tunnels.RunningCount;
            label.Text = running > 0 ? $"Tunnels · {running}" : "Tunnels";
        }
    }

    public void Activate()
    {
        View.Focus();
        Post(new { t = "focus" });
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Disconnect("Closed.");
        _endpoint?.Dispose();
        View.Dispose();
    }
}
