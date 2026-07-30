using Kontena.Core.Shell;
using Kontena.Sdk.Models;

namespace Kontena.App;

/// <summary>
/// The shells opened from cluster mode, one per context, kept for as long as the window is (KON-171).
/// <para>
/// Navigating away from the Terminal page tears down its view, not its shell. A terminal that restarted
/// every time you looked at a pod is one you cannot leave a build running in, and the shell being alive
/// is only half of it: without something reading the output, the pipe fills and the command blocks — so
/// <see cref="RetainedShellSession"/> keeps reading whether or not anyone is watching, and hands the
/// next view the screen as it was.
/// </para>
/// <para>
/// Keyed by backend id, which is what "per context" means here: two clusters are two shells, and coming
/// back to either finds the one you left.
/// </para>
/// </summary>
public sealed class ClusterTerminals : IAsyncDisposable
{
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private sealed record Entry(RetainedShellSession Session, ClusterShellRequest Request);

    /// <summary>
    /// The shell for <paramref name="backend"/>, started if there is not one already.
    /// </summary>
    public async ValueTask<IExecSession> OpenAsync(
        string backend, ClusterShellRequest request, int columns, int rows, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(backend, out var existing))
        {
            if (!existing.Session.HasEnded)
                return existing.Session;

            // Typed exit, or the shell fell over. Reattaching to a dead one would show its last screen
            // and swallow every keystroke after it.
            _sessions.Remove(backend);
            await existing.Session.DisposeAsync().ConfigureAwait(false);
        }

        var session = RetainedShellSession.Retain(
            await HostShellLauncher.OpenAsync(request, columns, rows, ct).ConfigureAwait(false));

        _sessions[backend] = new Entry(session, request);
        return session;
    }

    /// <summary>
    /// What the kept shell for <paramref name="backend"/> was started with, or null if there is none.
    /// <para>
    /// The page shows this rather than what the pickers say now. A shell's context and namespace are
    /// fixed when it starts — changing the namespace afterwards does not reach into a running shell, and
    /// a header claiming otherwise would be telling the user something <c>kubectl</c> disagrees with.
    /// Reconnect is how you get one on the current selection.
    /// </para>
    /// </summary>
    public ClusterShellRequest? RequestFor(string backend) =>
        _sessions.TryGetValue(backend, out var entry) && !entry.Session.HasEnded ? entry.Request : null;

    /// <summary>Let go of the view without ending the shell.</summary>
    public void Detach(string backend)
    {
        if (_sessions.TryGetValue(backend, out var entry))
            entry.Session.Detach();
    }

    /// <summary>End the shell for <paramref name="backend"/> — what Reconnect asks for.</summary>
    public async ValueTask DiscardAsync(string backend)
    {
        if (!_sessions.Remove(backend, out var entry))
            return;

        await entry.Session.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _sessions.Values)
            await entry.Session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
    }
}
