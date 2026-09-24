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
    /// <summary>
    /// The whole line as one line of text, in the order it is drawn.
    /// </summary>
    /// <param name="withTimestamp">
    /// Whether to lead with the timestamp — the console's own Timestamps toggle, so a copy says what
    /// the screen says. A line with no timestamp of its own ignores it.
    /// </param>
    string ForClipboard(bool withTimestamp);
}
