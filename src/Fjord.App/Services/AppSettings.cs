using Fjord.Core.Storage;

namespace Fjord.Services;

/// <summary>Global preferences in %APPDATA%\Fjord\settings.json. Add a property → it persists.</summary>
public sealed class AppSettings
{
    public string FontFamily { get; set; } = "'Cascadia Mono', Consolas, monospace";
    public int FontSize { get; set; } = 14;
    public int Scrollback { get; set; } = 10000;
    public bool CopyOnSelect { get; set; } = true;
    public bool CursorBlink { get; set; } = true;
    /// <summary>Default terminal colour scheme (a name from <see cref="ColorSchemes"/>). Sessions can override it.</summary>
    public string ColorScheme { get; set; } = ColorSchemes.DefaultName;
    /// <summary>System, Light or Dark (window chrome; the terminal itself stays dark).</summary>
    public string Theme { get; set; } = "System";
    public string DefaultUsername { get; set; } = "";
    public string? LastDownloadFolder { get; set; }
    /// <summary>SFTP tabs show a local (this PC) file pane next to the server's files.</summary>
    public bool SftpTwoPane { get; set; }
    /// <summary>Folder the local pane opens in (where it was last).</summary>
    public string? LastLocalFolder { get; set; }

    private static string PathOnDisk => AppPaths.File("settings.json");

    /// <summary>Raised after <see cref="Save"/>, so open windows can apply changes right away.</summary>
    public event EventHandler? Saved;

    public static AppSettings Load() => JsonStore.Load<AppSettings>(PathOnDisk);

    public void Save()
    {
        JsonStore.Save(PathOnDisk, this);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
