using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Core.Models;
using Kontena.Core.Shell;
using Kontena.Sdk.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// One terminal on this machine, started on the cluster currently shown with <c>k</c> aliased to
/// <c>kubectl</c> (KON-171, KON-216).
/// <para>
/// The page is the terminal, so unlike container and pod detail there is no tab of its own to wait for
/// and <see cref="IsTerminalSelected"/> is simply true — the tab strip decides which of these exists at
/// all. Everything below it — the exec session, the terminal control, the font settings — is the
/// machinery the container shells already use.
/// </para>
/// <para>
/// The session belongs to <see cref="ClusterTerminals"/> rather than to this view model, so navigating
/// away and back finds the shell still running with its screen intact. That is also why this can be
/// rebuilt freely: it holds no state a rebuild could lose.
/// </para>
/// </summary>
public sealed partial class ClusterTerminalViewModel : ViewModelBase, ITerminalHost
{
    private readonly ClusterTerminal _terminal;

    public ClusterTerminalViewModel(ClusterTerminal terminal, TerminalFont terminalFont)
    {
        _terminal = terminal;

        terminal.DetachedChanged += () => OnPropertyChanged(nameof(IsDetached));

        TerminalFontFamily = $"{terminalFont.Family}, monospace";
        TerminalFontSize = terminalFont.Size;
        TerminalLigatures = terminalFont.Ligatures;
        ShellLabel = System.IO.Path.GetFileName(HostShell.Detect());
    }

    /// <summary>Whether this is the tab being shown, for the strip that draws them.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Whether this terminal is showing in a window of its own (KON-217).</summary>
    public bool IsDetached
    {
        get => _terminal.IsDetached;
        set => _terminal.IsDetached = value;
    }

    /// <summary>What the tab is called.</summary>
    public string Title => _terminal.Title;

    /// <summary>The context this shell runs on.</summary>
    public string Context => _terminal.Request.Context;

    /// <summary>The namespace it was started in, when one is pinned.</summary>
    public string? Namespace => _terminal.Request.Namespace;

    /// <summary>
    /// Whether the namespace could be pinned. It cannot be when the context's cluster or user is
    /// unknown, and saying so beats a header that claims a namespace <c>kubectl</c> will not use.
    /// </summary>
    public bool HasNamespace => !string.IsNullOrEmpty(Namespace);

    /// <summary>The terminal this page drives, for the tab strip that owns it.</summary>
    internal ClusterTerminal Terminal => _terminal;

    public string TerminalFontFamily { get; }

    public double TerminalFontSize { get; }

    public bool TerminalLigatures { get; }

    public string ShellLabel { get; }

    public bool IsTerminalSelected => true;

    public bool CanOpenTerminal => !string.IsNullOrWhiteSpace(_terminal.Request.Context);

    /// <summary>
    /// Open the shell, or reattach to the one already running for this terminal. The size is provisional
    /// either way — the terminal control resizes the PTY to its real grid the moment it has one, which is
    /// the first thing <c>TerminalView</c> does after this returns.
    /// </summary>
    public ValueTask<IExecSession> OpenExecSessionAsync(CancellationToken ct) =>
        _terminal.OpenAsync(columns: 120, rows: 30, ct);

    /// <inheritdoc/>
    public ValueTask ReleaseExecSessionAsync(IExecSession session, bool discard)
    {
        if (discard)
            return _terminal.EndAsync();

        _terminal.Detach();
        return ValueTask.CompletedTask;
    }
}
