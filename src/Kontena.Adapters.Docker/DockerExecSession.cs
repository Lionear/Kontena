using System.Runtime.CompilerServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Kontena.Sdk.Models;
using Kontena.Sdk;

namespace Kontena.Adapters.Docker;

/// <summary>
/// An <see cref="IExecSession"/> backed by a Docker exec attached over a
/// <see cref="MultiplexedStream"/>. With a TTY the engine merges stdout/stderr,
/// so output is read as one raw byte stream and input is written straight back.
/// </summary>
internal sealed class DockerExecSession(DockerClient client, string execId, MultiplexedStream stream)
    : IExecSession
{
    private int? _exitCode;

    public int? ExitCode => _exitCode;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            MultiplexedStream.ReadResult read;
            try
            {
                read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (read.EOF)
            {
                await CaptureExitAsync().ConfigureAwait(false);
                yield break;
            }

            if (read.Count > 0)
                yield return buffer.AsMemory(0, read.Count).ToArray();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
    {
        var bytes = input.ToArray();
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        if (columns <= 0 || rows <= 0)
            return;

        try
        {
            await client.Exec.ResizeContainerExecTtyAsync(execId, new ContainerResizeParameters
            {
                Width = (uint)columns,
                Height = (uint)rows,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a stopped or non-TTY exec may reject a resize.
        }
    }

    private async ValueTask CaptureExitAsync()
    {
        try
        {
            var inspect = await client.Exec.InspectContainerExecAsync(execId, CancellationToken.None).ConfigureAwait(false);
            if (!inspect.Running)
                _exitCode = (int)inspect.ExitCode;
        }
        catch
        {
            // Best-effort exit capture — the session ends regardless.
        }
    }

    public ValueTask DisposeAsync()
    {
        try { stream.CloseWrite(); }
        catch { /* already closed */ }

        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
