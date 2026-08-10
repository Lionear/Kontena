using System.Diagnostics;
using Avalonia.Threading;

namespace Kontena.App.Services;

/// <summary>
/// A timing trace for the app's own startup and navigation, off unless <c>KONTENA_TRACE=1</c>.
/// <para>
/// It exists because "the app feels slow" cannot be argued about, only measured, and every attempt to
/// measure it from outside answered the wrong question: a profiler says which method burned CPU, while
/// what a user calls slow is the wait between a click and the screen catching up — spent, more often
/// than not, waiting on a cluster rather than burning anything. KON-352 was diagnosed with this and
/// KON-354 was measured against it; leaving it in means the next round starts with a number instead of
/// a rebuild.
/// </para>
/// <para>
/// Deliberately not a logging framework. Times go to stderr, so a traced run needs a redirect and
/// nothing else, and every entry is milliseconds since the process started — the clock the question is
/// actually asked in ("three seconds before I could do anything"), not one that starts at some point
/// the app chose.
/// </para>
/// <para>
/// Every call is a no-op when tracing is off, including the string it would have formatted:
/// <see cref="Mark"/> takes the message already built, so a mark on a hot path wants an interpolated
/// string only where one is cheap. The marks that exist sit on startup and navigation, which run once
/// per click at most.
/// </para>
/// </summary>
public static class Diag
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("KONTENA_TRACE") == "1";

    private static readonly DateTime Started = ProcessStart();

    /// <summary>
    /// When this process began. Guarded because a diagnostic must not be the thing that stops the app
    /// from starting: this is a static initialiser, so a platform that refuses to answer would throw
    /// before <c>Main</c> and take the whole app with it — over a feature nobody switched on. Where it
    /// cannot be had, times run from the first mark instead, which costs the startup offset and
    /// nothing else.
    /// </summary>
    private static DateTime ProcessStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime;
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    public static void Mark(string label)
    {
        if (Enabled)
            Console.Error.WriteLine($"[trace] {(DateTime.Now - Started).TotalMilliseconds,9:F1}  {label}");
    }

    /// <summary>Time one await, and report both when it ended and how long it took.</summary>
    public static async Task<T> TimeAsync<T>(string label, Task<T> work)
    {
        if (!Enabled)
            return await work;

        var started = Stopwatch.StartNew();
        var result = await work;
        Mark($"{label} took {started.Elapsed.TotalMilliseconds:F1} ms");
        return result;
    }

    /// <inheritdoc cref="TimeAsync{T}"/>
    public static async Task TimeAsync(string label, Task work)
    {
        if (!Enabled)
        {
            await work;
            return;
        }

        var started = Stopwatch.StartNew();
        await work;
        Mark($"{label} took {started.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <inheritdoc cref="TimeAsync{T}"/>
    public static void Time(string label, Action work)
    {
        if (!Enabled)
        {
            work();
            return;
        }

        var started = Stopwatch.StartNew();
        work();
        Mark($"{label} took {started.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <inheritdoc cref="TimeAsync{T}"/>
    public static T Time<T>(string label, Func<T> work)
    {
        if (!Enabled)
            return work();

        var started = Stopwatch.StartNew();
        var result = work();
        Mark($"{label} took {started.Elapsed.TotalMilliseconds:F1} ms");
        return result;
    }

    /// <summary>
    /// Report every stall on the UI thread longer than <paramref name="thresholdMs"/>.
    /// <para>
    /// A timer that should tick every 50 ms cannot tick while the dispatcher is busy, so the gap it
    /// missed is the measurement. This is the one number that matches what "feels slow" describes —
    /// a total that says how long an operation took cannot tell a wait that blocked the window from a
    /// wait that did not, and only the first is felt. It is what caught KON-354: the sidebar's counts
    /// froze the window for 150–330 ms, several times a minute, while nothing on screen changed.
    /// </para>
    /// </summary>
    public static void WatchUiThread(int thresholdMs = 100)
    {
        if (!Enabled)
            return;

        var since = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

        timer.Tick += (_, _) =>
        {
            var gap = since.Elapsed.TotalMilliseconds;
            since.Restart();

            if (gap > thresholdMs)
                Mark($"!! UI thread stalled {gap:F0} ms");
        };

        timer.Start();
    }
}
