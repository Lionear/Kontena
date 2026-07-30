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
/// </summary>
public sealed class ClusterTerminalViewModel : ViewModelBase, ITerminalHost
{
    private readonly ClusterShellRequest _request;

    public ClusterTerminalViewModel(ClusterShellRequest request, TerminalFont terminalFont)
    {
        _request = request;

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;
        ShellLabel = System.IO.Path.GetFileName(HostShell.Detect());

        Context = request.Context;
        Namespace = request.Namespace;
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

    public bool CanOpenTerminal => !string.IsNullOrWhiteSpace(_request.Context);

    /// <summary>
    /// Open the shell. The size is provisional — the terminal control resizes the PTY to its real grid
    /// the moment it has one, which is the first thing <c>TerminalView</c> does after this returns.
    /// </summary>
    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) =>
        HostShellLauncher.OpenAsync(_request, columns: 120, rows: 30, ct);
}
