using System.IO;
using Fjord.Core.Profiles;
using Fjord.Core.Ssh;
using Fjord.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Fjord.Services;

/// <summary>App-wide singletons. Kept deliberately simple (no DI container) — add new services here.</summary>
public static class AppServices
{
    public static ProfileStore Profiles { get; } = new();
    public static KnownHostsStore KnownHosts { get; } = new();
    public static Fjord.Core.Snippets.SnippetStore Snippets { get; } = new();
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static ISecretProtector Protector { get; } = new DpapiProtector();

    /// <summary>Folder with terminal.html / guide.html, mapped to https://fjord.local/ inside WebView2.</summary>
    public static string AssetsFolder => Path.Combine(AppContext.BaseDirectory, "Assets");
    public const string AssetsHost = "fjord.local";

    private static Task<CoreWebView2Environment>? _webViewEnv;

    /// <summary>One shared WebView2 environment (one browser process) for all tabs.</summary>
    public static Task<CoreWebView2Environment> WebViewEnvironment =>
        _webViewEnv ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(AppPaths.LocalDirectory, "WebView2"));

    /// <summary>Common WebView2 setup: local assets, no browser chrome/shortcuts.</summary>
    public static async Task InitWebViewAsync(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        await view.EnsureCoreWebView2Async(await WebViewEnvironment);
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(AssetsHost, AssetsFolder, CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        // Links never navigate inside the app — open them in the real browser.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInBrowser(e.Uri); };
    }

    public static void OpenInBrowser(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
