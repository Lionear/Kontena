using System.Globalization;
using Kontena.Core.Shell;
using Kontena.Sdk.Models;

namespace Kontena.App;

/// <summary>
/// One open terminal: a shell of its own, and the cluster and namespace it was started on.
/// <para>
/// Its context and namespace are fixed here, at the moment it is opened. Moving the namespace picker
/// afterwards does not reach into a shell that is already running, and with several terminals side by
/// side that is visible rather than theoretical — two of them can be on different namespaces while the
/// picker shows one. So each shows its own.
/// </para>
/// </summary>
public sealed class ClusterTerminal(string backend, string id, string title, ClusterShellRequest request)
{
    public string Backend { get; } = backend;

    public string Id { get; } = id;

    /// <summary>What the tab is called. Numbered per cluster, plus the namespace when one is pinned.</summary>
    public string Title { get; } = title;

    public ClusterShellRequest Request { get; } = request;

    private RetainedShellSession? _session;
    private bool _detached;

    /// <summary>
    /// Whether this terminal is showing in a window of its own (KON-217).
    /// <para>
    /// It moves rather than mirrors: <see cref="RetainedShellSession"/> serves one viewer at a time on
    /// purpose, and two emulators rendering the same bytes would disagree the moment their windows were
    /// different sizes — a PTY has one size, so one of the two would be showing nonsense.
    /// </para>
    /// </summary>
    public bool IsDetached
    {
        get => _detached;
        set
        {
            if (_detached == value)
                return;

            _detached = value;
            DetachedChanged?.Invoke();
        }
    }

    /// <summary>
    /// Raised when it moves in or out of its own window. The page listens because the window outlives
    /// it: closing the window has to put the terminal back on a page that may already be open.
    /// </summary>
    public event Action? DetachedChanged;

    /// <summary>
    /// Raised when the terminal is closed for good. Its window listens: closing the tab of a terminal
    /// that is off in a window of its own would otherwise leave that window standing with a shell that
    /// has already been torn down.
    /// </summary>
    public event Action? Closed;

    internal void RaiseClosed() => Closed?.Invoke();

    /// <summary>True while a shell is running for this terminal.</summary>
    public bool IsRunning => _session is { HasEnded: false };

    /// <summary>The shell, started if it is not running yet.</summary>
    public async ValueTask<IExecSession> OpenAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (_session is { } existing)
        {
            if (!existing.HasEnded)
                return existing;

            // Typed exit, or the shell fell over. Reattaching to a dead one would show its last screen
            // and swallow every keystroke after it.
            _session = null;
            await existing.DisposeAsync().ConfigureAwait(false);
        }

        _session = RetainedShellSession.Retain(
            await HostShellLauncher.OpenAsync(Request, columns, rows, ct).ConfigureAwait(false));

        return _session;
    }

    /// <summary>Let go of the view without ending the shell.</summary>
    public void Detach() => _session?.Detach();

    /// <summary>End the shell but keep the terminal — what Reconnect asks for.</summary>
    public async ValueTask EndAsync()
    {
        var session = _session;
        _session = null;

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The shells opened from cluster mode, kept for as long as the window is (KON-171, KON-216).
/// <para>
/// Navigating away from the Terminal page tears down its view, not its shell. A terminal that restarted
/// every time you looked at a pod is one you cannot leave a build running in, and the shell being alive
/// is only half of it: without something reading the output, the pipe fills and the command blocks — so
/// <see cref="RetainedShellSession"/> keeps reading whether or not anyone is watching, and hands the
/// next view the screen as it was.
/// </para>
/// <para>
/// Keyed per cluster and then per terminal, so a cluster can have several and coming back to any of them
/// finds the one you left.
/// </para>
/// </summary>
public sealed class ClusterTerminals : IAsyncDisposable
{
    private readonly List<ClusterTerminal> _terminals = [];
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);

    /// <summary>The terminals open on <paramref name="backend"/>, in the order they were opened.</summary>
    public IReadOnlyList<ClusterTerminal> For(string backend) =>
        [.. _terminals.Where(t => t.Backend == backend)];

    /// <summary>How many are open on <paramref name="backend"/> — the number the sidebar shows.</summary>
    public int CountFor(string backend) => _terminals.Count(t => t.Backend == backend);

    /// <summary>
    /// Open another terminal on <paramref name="backend"/>. The shell itself starts when a view first
    /// attaches, so opening a tab costs nothing until it is looked at.
    /// </summary>
    public ClusterTerminal Add(string backend, ClusterShellRequest request)
    {
        // Numbered by a counter that only goes up. Reusing the number of a closed terminal would put
        // "Terminal 1" back on the screen as something that shares nothing with the one just closed.
        var number = _counters.TryGetValue(backend, out var last) ? last + 1 : 1;
        _counters[backend] = number;

        var title = "Terminal " + number.ToString(CultureInfo.InvariantCulture);
        if (request.Namespace is { Length: > 0 } ns)
            title += " · " + ns;

        var terminal = new ClusterTerminal(
            backend, backend + "#" + number.ToString(CultureInfo.InvariantCulture), title, request);

        _terminals.Add(terminal);
        _selected[backend] = terminal.Id;
        return terminal;
    }

    /// <summary>Which terminal was last looked at on this cluster, so returning to the page lands there.</summary>
    public string? SelectedFor(string backend) =>
        _selected.TryGetValue(backend, out var id) && _terminals.Any(t => t.Id == id) ? id : null;

    /// <summary>Remember the terminal the user switched to.</summary>
    public void Select(ClusterTerminal terminal) => _selected[terminal.Backend] = terminal.Id;

    /// <summary>
    /// Close the tab and end its shell. A terminal with no tab is one nobody can reach again, so leaving
    /// the shell running would only hide it.
    /// </summary>
    public async ValueTask CloseAsync(ClusterTerminal terminal)
    {
        _terminals.Remove(terminal);

        if (_selected.TryGetValue(terminal.Backend, out var selected) && selected == terminal.Id)
            _selected.Remove(terminal.Backend);

        terminal.RaiseClosed();
        await terminal.EndAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var terminal in _terminals)
            await terminal.EndAsync().ConfigureAwait(false);

        _terminals.Clear();
    }
}
