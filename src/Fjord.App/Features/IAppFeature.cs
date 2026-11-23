using Fjord.Views;

namespace Fjord.Features;

public enum FeatureScope
{
    /// <summary>Button in the sidebar's tool list (Key generator, Settings…).</summary>
    Global,
    /// <summary>Button in every terminal tab's toolbar (SFTP, Install key…). Context has a terminal.</summary>
    Session,
}

/// <summary>
/// A pluggable command. To add a feature: implement this, then add one line to <see cref="FeatureRegistry"/>.
/// </summary>
public interface IAppFeature
{
    string Title { get; }
    /// <summary>Segoe Fluent Icons code point, e.g. "\uE8D7". Browse them: https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font</summary>
    string Glyph { get; }
    FeatureScope Scope { get; }
    string? ToolTip => null;

    void Execute(FeatureContext context);
}

/// <summary>What a feature can reach: the main window (open tabs, dialogs) and, for session features, the terminal.</summary>
public sealed record FeatureContext(MainWindow Shell, TerminalTab? Terminal = null);
