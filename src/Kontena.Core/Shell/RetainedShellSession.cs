using System.Threading.Channels;
using Kontena.Sdk.Models;

namespace Kontena.Core.Shell;

/// <summary>
/// A shell that outlives the page showing it: it keeps running while you are somewhere else, and the
/// next view to attach gets the screen back as it was (KON-171).
/// <para>
/// Two things have to be true for that. The output must keep being read even with nobody watching —
/// otherwise the pipe fills and the shell blocks mid-command, so a long-running job would stop the
/// moment you navigated away. And what has already been printed has to be kept, because a live shell
/// attached to a blank screen still looks like it restarted.
/// </para>
/// <para>
/// So one pump reads the session for its whole life, appending to a transcript and forwarding to
/// whichever view is attached, if any. Attaching replays the transcript first and then continues live,
/// which is why <see cref="ReadOutputAsync"/> may be called again and again where the underlying
/// session allows it only once.
/// </para>
/// </summary>
public sealed class RetainedShellSession : IExecSession
{
    /// <summary>
    /// How much scrollback to keep for a reattach. Enough for the screens anyone scrolls back through,
    /// bounded because a shell left running for a day should not grow without limit.
    /// </summary>
    private const int TranscriptLimit = 1 << 20;

    private readonly IExecSession _inner;
    private readonly CancellationTokenSource _pump = new();
    private readonly Lock _gate = new();
    private readonly List<byte> _transcript = [];

    private Channel<ReadOnlyMemory<byte>>? _attached;
    private bool _ended;

    private RetainedShellSession(IExecSession inner) => _inner = inner;

    /// <summary>Wrap <paramref name="inner"/> and start reading it immediately.</summary>
    public static RetainedShellSession Retain(IExecSession inner)
    {
        var session = new RetainedShellSession(inner);
        _ = session.PumpAsync();
        return session;
    }

    /// <summary>True once the shell has exited; a spent session should be replaced, not reattached.</summary>
    public bool HasEnded
    {
        get { lock (_gate) return _ended; }
    }

    /// <inheritdoc/>
    public int? ExitCode => _inner.ExitCode;

    /// <inheritdoc/>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Channel<ReadOnlyMemory<byte>> channel;
        byte[] replay;
        bool ended;

        lock (_gate)
        {
            // One viewer at a time. The previous one is finished rather than left hanging: its view is
            // gone, and a channel nobody reads is a slow leak of everything the shell prints.
            _attached?.Writer.TryComplete();

            channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            _attached = channel;
            replay = [.. _transcript];
            ended = _ended;
        }

        if (replay.Length > 0)
            yield return replay;

        if (ended)
            yield break;

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) =>
        _inner.WriteAsync(input, ct);

    /// <inheritdoc/>
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default) =>
        _inner.ResizeAsync(columns, rows, ct);

    /// <summary>
    /// Stop reading and let go of the view without touching the shell. What the page does on the way
    /// out, so the session is there again on the way back.
    /// </summary>
    public void Detach()
    {
        lock (_gate)
        {
            _attached?.Writer.TryComplete();
            _attached = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _pump.CancelAsync().ConfigureAwait(false);
        Detach();
        _pump.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _inner.ReadOutputAsync(_pump.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    Append(chunk.Span);
                    _attached?.Writer.TryWrite(chunk);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The shell going away mid-read is an ending, and the ending is recorded below.
        }

        lock (_gate)
        {
            _ended = true;
            _attached?.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Append to the transcript, trimming the front once it is too long.
    /// <para>
    /// Trimmed to a line boundary rather than an exact byte count: the transcript is a stream of escape
    /// sequences as much as text, and cutting through the middle of one leaves the replay painting the
    /// rest of the screen in whatever colour that sequence was half of. Sequences do not span newlines,
    /// so a newline is a place it is safe to cut.
    /// </para>
    /// </summary>
    private void Append(ReadOnlySpan<byte> chunk)
    {
        _transcript.AddRange(chunk);

        if (_transcript.Count <= TranscriptLimit)
            return;

        var excess = _transcript.Count - TranscriptLimit;
        var cut = _transcript.IndexOf((byte)'\n', excess);

        // No newline left to cut at — drop the excess as it is rather than keep growing.
        _transcript.RemoveRange(0, cut >= 0 ? cut + 1 : excess);
    }
}
