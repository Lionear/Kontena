using System.Text;
using System.Threading.Channels;
using Kontena.Core.Shell;
using Kontena.Sdk.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// A shell that survives leaving the page it is shown on (KON-171).
/// </summary>
public sealed class RetainedShellSessionTests
{
    [Fact]
    public async Task Reattaching_replays_what_was_printed_before()
    {
        var inner = new ScriptedSession();
        await using var session = RetainedShellSession.Retain(inner);

        inner.Emit("first\n");
        await inner.Drained();

        Assert.Equal("first\n", await ReadAvailableAsync(session));

        // The view goes away and comes back: the shell never stopped, so neither should the screen.
        session.Detach();
        Assert.Equal("first\n", await ReadAvailableAsync(session));
    }

    /// <summary>
    /// The reason the pump exists. With nobody attached the output still has to be read, or the pipe
    /// fills and the command blocks — a build left running while you look at a pod would simply stop.
    /// </summary>
    [Fact]
    public async Task Output_is_read_while_nothing_is_attached()
    {
        var inner = new ScriptedSession();
        await using var session = RetainedShellSession.Retain(inner);

        inner.Emit("while you were away\n");
        await inner.Drained();

        Assert.Equal("while you were away\n", await ReadAvailableAsync(session));
    }

    [Fact]
    public async Task A_reattached_view_sees_what_comes_next_as_well()
    {
        var inner = new ScriptedSession();
        await using var session = RetainedShellSession.Retain(inner);

        inner.Emit("before\n");
        await inner.Drained();
        Assert.Equal("before\n", await ReadAvailableAsync(session));
        session.Detach();

        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();

        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("before\n", Encoding.UTF8.GetString(enumerator.Current.Span));

            inner.Emit("after\n");

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("after\n", Encoding.UTF8.GetString(enumerator.Current.Span));
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// A shell the user exited must not be handed out again: reattaching would show its last screen and
    /// swallow every keystroke after it.
    /// </summary>
    [Fact]
    public async Task A_shell_that_exited_reports_that_it_has_ended()
    {
        var inner = new ScriptedSession();
        await using var session = RetainedShellSession.Retain(inner);

        Assert.False(session.HasEnded);

        inner.Close();
        await inner.Drained();

        // The pump notices on its next read rather than instantly.
        for (var i = 0; i < 50 && !session.HasEnded; i++)
            await Task.Delay(20);

        Assert.True(session.HasEnded);
    }

    private static async Task<string> ReadAvailableAsync(RetainedShellSession session)
    {
        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();

        try
        {
            return await enumerator.MoveNextAsync()
                ? Encoding.UTF8.GetString(enumerator.Current.Span)
                : string.Empty;
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>An exec session whose output the test writes, standing in for a shell.</summary>
    private sealed class ScriptedSession : IExecSession
    {
        private readonly Channel<ReadOnlyMemory<byte>> _output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public int? ExitCode { get; private set; }

        public void Emit(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void Close()
        {
            ExitCode = 0;
            _output.Writer.TryComplete();
        }

        /// <summary>Waits until the pump has taken everything written so far.</summary>
        public async Task Drained()
        {
            for (var i = 0; i < 100 && _output.Reader.Count > 0; i++)
                await Task.Delay(10);

            // The pump has read it; give it the moment it needs to append and forward.
            await Task.Delay(20);
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) =>
            _output.Reader.ReadAllAsync(ct);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
