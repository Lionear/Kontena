using Kontena.App.Services;

namespace Kontena.App.Tests;

/// <summary>
/// The trace is off unless asked for, and off means invisible (KON-352). A diagnostic that changed
/// what the app does, or wrote to a console nobody asked to read, would be worse than no diagnostic:
/// it sits on the startup path, so anything it gets wrong is wrong for every run.
/// </summary>
public sealed class DiagTests
{
    [Fact]
    public void Is_off_unless_the_environment_asks_for_it()
    {
        // The test run does not set KONTENA_TRACE, which is the state every normal run is in.
        Assert.False(Diag.Enabled);
    }

    [Fact]
    public async Task Hands_back_what_it_wraps_either_way()
    {
        // Timing something must not change it — the value, and the fact that the work ran at all.
        var ran = false;

        Assert.Equal(7, Diag.Time("value", () => 7));
        Assert.Equal(7, await Diag.TimeAsync("task", Task.FromResult(7)));

        Diag.Time("action", () => ran = true);
        Assert.True(ran);

        ran = false;
        await Diag.TimeAsync("task without a result", Task.Run(() => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void Writes_nothing_while_it_is_off()
    {
        var stderr = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            Diag.Mark("this must not appear");
            Diag.WatchUiThread();
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.Equal(string.Empty, captured.ToString());
    }
}
