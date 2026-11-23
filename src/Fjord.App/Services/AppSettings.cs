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
    /// <summary>System, Light or Dark (window chrome; the terminal itself stays dark).</summary>
    public string Theme { get; set; } = "System";
    public string DefaultUsername { get; set; } = "";
    public string? LastDownloadFolder { get; set; }

    private static string PathOnDisk => AppPaths.File("settings.json");

    public static AppSettings Load() => JsonStore.Load<AppSettings>(PathOnDisk);

    public void Save() => JsonStore.Save(PathOnDisk, this);
}
