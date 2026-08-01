using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using k8s;
using Kontena.Sdk.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// An <see cref="IExecSession"/> over a pod exec WebSocket (KON-97).
/// <para>
/// The same interface the CEAL uses for container exec, which is the point: pod-detail's Shell tab
/// and the container terminal share one view because the duplex PTY channel really is identical.
/// Kubernetes multiplexes it over channels — stdout, stdin, resize, and an error channel that
/// delivers the process's exit status — so a <see cref="IStreamDemuxer"/> sits between the socket
/// and the byte stream the terminal reads.
/// </para>
/// </summary>
internal sealed class KubernetesExecSession : IExecSession
{
    private readonly IStreamDemuxer _demuxer;
    private readonly Stream _output;
    private readonly Stream _input;
    private int? _exitCode;

    private KubernetesExecSession(IStreamDemuxer demuxer, Stream output, Stream input)
    {
        _demuxer = demuxer;
        _output = output;
        _input = input;
    }

    public static async Task<IExecSession> OpenAsync(
        IKubernetes client, string pod, string? ns, string container, ExecRequest request, CancellationToken ct)
    {
        var command = request.Command.Count > 0 ? request.Command : ["/bin/sh"];

        var demuxer = await client.MuxedStreamNamespacedPodExecAsync(
            pod, ns, command, container,
            stderr: true, stdin: true, stdout: true, tty: request.Tty,
            cancellationToken: ct).ConfigureAwait(false);

        demuxer.Start();

        // Two half-streams rather than one duplex stream: a stream that both reads stdout and writes
        // stdin blocks on the first write, so reading and writing each get their own channel view.
        var output = demuxer.GetStream(ChannelIndex.StdOut, null);
        var input = demuxer.GetStream(null, ChannelIndex.StdIn);
        return new KubernetesExecSession(demuxer, output, input);
    }

    public int? ExitCode => _exitCode;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _output.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception)
            {
                // The socket closing is how a finished session ends, not an error to surface.
                yield break;
            }

            if (read <= 0)
                yield break;

            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
    {
        if (input.Length == 0)
            return;

        await _input.WriteAsync(input, ct).ConfigureAwait(false);
        await _input.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resize the remote PTY. Kubernetes takes this as a JSON frame on its own channel rather than
    /// as a control sequence in the byte stream.
    /// </summary>
    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (columns <= 0 || rows <= 0)
            return;

        try
        {
            var frame = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new ResizeFrame(columns, rows)));
            await _demuxer.Write(ChannelIndex.Resize, frame, 0, frame.Length, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort, exactly as the interface promises: a failed resize must not kill the shell.
        }
    }

    /// <summary>The wire shape Kubernetes expects; the capitalised names are part of the protocol.</summary>
    private sealed record ResizeFrame(int Width, int Height);

    public ValueTask DisposeAsync()
    {
        _exitCode ??= 0;
        _input.Dispose();
        _output.Dispose();
        _demuxer.Dispose();
        return ValueTask.CompletedTask;
    }
}
