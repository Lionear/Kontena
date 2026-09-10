namespace Kontena.App.ViewModels;

/// <summary>
/// A console line that can go on the clipboard (KON-463).
/// <para>
/// The three log viewers draw two unrelated line types — <see cref="LogLineViewModel"/> and
/// <see cref="ComposeLogLine"/> — and the multi-row copy is one shared behaviour on the
/// <c>ListBox</c>, which sees only <c>object</c>. This is the one thing it needs from a line.
/// </para>
/// </summary>
public interface ILogLine
{
    /// <summary>The whole line as one line of text, in the order it is drawn.</summary>
    string ForClipboard { get; }
}
