using System.Windows;
using Fjord.Services;

namespace Fjord.Views;

/// <summary>
/// Non-modal help window showing a page from Assets/guide. Edit the HTML to change the text — no C# changes needed.
/// One window per page; asking again brings the existing one to the front.
/// </summary>
public partial class GuideWindow : Window
{
    public const string KeysPage = "guide.html";
    public const string TunnelsPage = "tunnels.html";

    private static readonly Dictionary<string, GuideWindow> Open = [];
    private readonly string _page;

    public static void ShowSingle(Window owner, string page = KeysPage)
    {
        if (Open.TryGetValue(page, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new GuideWindow(page) { Owner = owner };
        Open[page] = window;
        window.Show();
    }

    private GuideWindow(string page)
    {
        InitializeComponent();
        _page = page;
        Title = page == TunnelsPage ? "Guide — tunnels (port forwarding)" : "Guide — logging in with SSH keys";

        Loaded += async (_, _) =>
        {
            try
            {
                await AppServices.InitWebViewAsync(View);
                var dark = AppServices.Settings.Theme == "Dark" ||
                           AppServices.Settings.Theme == "System" && SystemUsesDarkMode();
                View.CoreWebView2.Navigate($"https://{AppServices.AssetsHost}/guide/{_page}" + (dark ? "?dark=1" : ""));
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "Could not open the guide: " + ex.Message);
            }
        };
        Closed += (_, _) => { View.Dispose(); Open.Remove(_page); };
    }

    private static bool SystemUsesDarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
