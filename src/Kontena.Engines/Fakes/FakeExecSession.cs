using System.Text;
using System.Threading.Channels;

namespace Kontena.Engines.Fakes;

/// <summary>
/// An in-memory <see cref="IExecSession"/> that emulates a tiny echo shell: it
/// prints a prompt, echoes what you type, and exits on the <c>exit</c> command.
/// Enough to drive the terminal UI at design-time and in tests without a real
/// engine.
/// </summary>
internal sealed class FakeExecSession : IExecSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _out =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly StringBuilder _line = new();
    private int? _exitCode;

    public FakeExecSession()
    {
        Emit("Kontena fake shell — echo mode. Type 'exit' to quit.\r\n");
        Emit("$ ");
    }

    public int? ExitCode => _exitCode;

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) =>
        _out.Reader.ReadAllAsync(ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
    {
        foreach (var ch in Encoding.UTF8.GetString(input.Span))
        {
            if (ch is '\r' or '\n')
            {
                Emit("\r\n");
                var command = _line.ToString().Trim();
                _line.Clear();

                if (command == "exit")
                {
                    _exitCode = 0;
                    _out.Writer.TryComplete();
                    return ValueTask.CompletedTask;
                }

                Emit("$ ");
            }
            else
            {
                _line.Append(ch);
                Emit(ch.ToString());
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _out.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Emit(string text) => _out.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
}
