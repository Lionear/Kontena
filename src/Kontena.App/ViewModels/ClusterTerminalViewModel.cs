using Kontena.Core.Models;
using Kontena.Core.Shell;
using Kontena.Sdk.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// A shell on this machine that starts on the cluster currently shown, with <c>k</c> aliased to
/// <c>kubectl</c> (KON-171).
/// <para>
/// The page is the terminal, so unlike container and pod detail there is no tab to select and
/// <see cref="IsTerminalSelected"/> is simply true. Everything below it — the exec session, the
/// terminal control, the font settings — is the machinery the container shells already use.
/// </para>
/// <para>
/// The session itself belongs to the shell rather than to this page, so navigating away and back finds
/// the shell still running with its screen intact. That is also why the page is rebuilt freely: it holds
/// no state a rebuild could lose.
/// </para>
/// </summary>
public sealed class ClusterTerminalViewModel : ViewModelBase, ITerminalHost
{
    private readonly Func<CancellationToken, ValueTask<IExecSession>> _open;
    private readonly Func<IExecSession, bool, ValueTask> _release;

    public ClusterTerminalViewModel(
        ClusterShellRequest request,
        TerminalFont terminalFont,
        Func<CancellationToken, ValueTask<IExecSession>> open,
        Func<IExecSession, bool, ValueTask> release)
    {
        _open = open;
        _release = release;

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;
        ShellLabel = System.IO.Path.GetFileName(HostShell.Detect());

        Context = request.Context;
        Namespace = request.Namespace;
        CanOpenTerminal = !string.IsNullOrWhiteSpace(request.Context);
    }

    /// <summary>The context this shell starts on, for the header.</summary>
    public string Context { get; }

    /// <summary>The namespace it starts in, when one is pinned.</summary>
    public string? Namespace { get; }

    /// <summary>
    /// Whether the namespace could be pinned. It cannot be when the context's cluster or user is
    /// unknown, and saying so beats a header that claims a namespace <c>kubectl</c> will not use.
    /// </summary>
    public bool HasNamespace => !string.IsNullOrEmpty(Namespace);

    public string TerminalFontFamily { get; }

    public double TerminalFontSize { get; }

    public bool TerminalLigatures { get; }

    public string ShellLabel { get; }

    public bool IsTerminalSelected => true;

    public bool CanOpenTerminal { get; }

    /// <summary>
    /// Open the shell, or reattach to the one already running for this cluster. The size is provisional
    /// either way — the terminal control resizes the PTY to its real grid the moment it has one, which is
    /// the first thing <c>TerminalView</c> does after this returns.
    /// </summary>
    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) => _open(ct);

    /// <inheritdoc/>
    public ValueTask ReleaseExecSessionAsync(IExecSession session, bool discard) => _release(session, discard);
}
