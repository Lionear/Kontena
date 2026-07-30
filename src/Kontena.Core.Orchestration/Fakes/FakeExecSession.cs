using System.Text;
using System.Threading.Channels;
using Kontena.Sdk.Models;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// A tiny in-memory <see cref="IExecSession"/> that emulates a pod shell: it prints a prompt,
/// echoes typed input, and exits on <c>exit</c>. Enough to drive the pod-detail terminal at
/// design-time and in tests without a real cluster. Mirrors the CEAL's fake exec session.
/// </summary>
internal sealed class FakeExecSession : IExecSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _out =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public FakeExecSession(string pod, string container)
    {
        Write($"Connected to {pod}/{container}\r\n$ ");
    }

    public int? ExitCode { get; private set; }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) =>
        _out.Reader.ReadAllAsync(ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
    {
        var text = Encoding.UTF8.GetString(input.Span);
        if (text.Trim() == "exit")
        {
            ExitCode = 0;
            _out.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        // Echo the keystrokes back, then a fresh prompt on newline.
        Write(text);
        if (text.Contains('\r') || text.Contains('\n'))
            Write("$ ");
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        ExitCode ??= 0;
        _out.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Write(string s) => _out.Writer.TryWrite(Encoding.UTF8.GetBytes(s));
}
