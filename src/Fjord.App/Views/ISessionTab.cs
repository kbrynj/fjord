using Fjord.Core.Profiles;

namespace Fjord.Views;

/// <summary>Anything that lives in a main-window tab (terminal, SFTP browser, future tools).</summary>
public interface ISessionTab
{
    string Title { get; }
    /// <summary>Segoe Fluent Icons glyph for the tab header.</summary>
    string Glyph { get; }
    SessionProfile Profile { get; }
    event EventHandler? TitleChanged;

    /// <summary>Tab became visible — move keyboard focus into it.</summary>
    void Activate();

    /// <summary>Tab is being closed — disconnect and release resources.</summary>
    void Close();
}
