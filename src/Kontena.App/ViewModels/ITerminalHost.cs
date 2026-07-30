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
    /// Open an attached shell session. Ownership (and disposal) passes to the caller — the
    /// terminal view drives it and tears it down.
    /// </summary>
    ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct);
}
