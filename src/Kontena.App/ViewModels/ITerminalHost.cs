using System.ComponentModel;
using Kontena.Sdk.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// What <c>TerminalView</c> needs from a detail page to host an interactive shell. Shared by
/// container detail (CEAL) and pod detail (OAL) — both open an <see cref="IExecSession"/> over
/// the same duplex PTY channel, so one terminal control drives either.
/// </summary>
public interface ITerminalHost : INotifyPropertyChanged
{
    /// <summary>Terminal font family (from settings).</summary>
    string TerminalFontFamily { get; }

    /// <summary>Terminal font size.</summary>
    double TerminalFontSize { get; }

    /// <summary>Whether font ligatures are enabled.</summary>
    bool TerminalLigatures { get; }

    /// <summary>
    /// What is running in the terminal, shown next to the status. A container shell is always
    /// <c>/bin/sh</c>; a shell on this machine is whichever one the user has, so the view asks rather
    /// than assumes.
    /// </summary>
    string ShellLabel { get; }

    /// <summary>True when the terminal/shell tab is the active one.</summary>
    bool IsTerminalSelected { get; }

    /// <summary>True when a shell can be opened right now (running + exec supported).</summary>
    bool CanOpenTerminal { get; }

    /// <summary>
    /// Open an attached shell session. The view drives it; what happens to it afterwards is decided by
    /// <see cref="ReleaseExecSessionAsync"/>, because that differs per page.
    /// </summary>
    ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct);

    /// <summary>
    /// Hand the session back when the view is done with it.
    /// <para>
    /// A container or pod shell ends here: it belongs to the page, and the page is gone. A shell on this
    /// machine does not — it keeps running so that leaving the page and coming back is leaving and
    /// coming back, not starting over.
    /// </para>
    /// </summary>
    /// <param name="discard">
    /// True when the user asked for a new session (Reconnect), which ends even a kept one. False when
    /// the view is merely going away.
    /// </param>
    ValueTask ReleaseExecSessionAsync(IExecSession session, bool discard);
}
