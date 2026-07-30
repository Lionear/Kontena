namespace Kontena.Sdk.Models;

/// <summary>
/// A live, attached exec session: a duplex byte channel to a process running
/// inside a container — typically an interactive shell with a pseudo-TTY.
/// <para>
/// Read output bytes from <see cref="ReadOutputAsync"/>, send input (keystrokes)
/// with <see cref="WriteAsync"/>, keep the PTY sized to the view with
/// <see cref="ResizeAsync"/>, and read <see cref="ExitCode"/> once the process
/// ends. Disposing tears the session down and detaches from the container.
/// </para>
/// This is the streaming counterpart to a one-shot exec (which just runs a
/// command and returns its exit code). Shared by both the CEAL (container exec)
/// and the OAL (pod exec) — the duplex PTY channel is identical.
/// </summary>
public interface IExecSession : IAsyncDisposable
{
    /// <summary>
    /// The raw output byte stream. With a TTY, stdout and stderr are merged into
    /// one stream by the engine. Chunks are yielded as they arrive; the sequence
    /// completes when the process exits or the session is disposed. Can only be
    /// enumerated once.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default);

    /// <summary>Send input bytes (e.g. keystrokes) to the process's stdin.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default);

    /// <summary>Resize the PTY to the given character grid. Best-effort.</summary>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default);

    /// <summary>The process exit code once it has terminated; null while it runs.</summary>
    int? ExitCode { get; }
}
