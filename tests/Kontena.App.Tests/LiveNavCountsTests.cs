using System.Globalization;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The sidebar's counts follow the streams the lists already follow (KON-339).
/// <para>
/// The lists went live in KON-250 and the badges beside them did not, so a workload that appeared
/// while you watched showed up as a new row next to a number that still said the old total. The page
/// knew; nothing carried it to the shell.
/// </para>
/// <para>
/// Driven by invoking the callback rather than by pushing a watch event and waiting out the 400ms
/// settle: what broke was the wiring between page and shell, and a test that sleeps to observe it
/// would be testing <c>Task.Delay</c>. That the settle fires the callback is the line right under it.
/// </para>
/// </summary>
public sealed class LiveNavCountsTests
{
    [Fact]
    public async Task A_cluster_page_that_sees_a_change_gets_the_badges_refreshed()
    {
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));
        shell.NavigateCommand.Execute("pods");

        var page = Assert.IsAssignableFrom<IClusterListPage>(shell.CurrentPage);
        Assert.NotNull(page.Changed);

        // Behind the app's back, the way kubectl would.
        var before = (await cluster.ListPodsAsync()).Count;
        await cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Pod, "app", "api-7d9c"));
        Assert.Equal(before - 1, (await cluster.ListPodsAsync()).Count);

        page.Changed!.Invoke();

        Assert.Equal(
            (before - 1).ToString(CultureInfo.InvariantCulture),
            shell.NavGroups.SelectMany(g => g.Items).Single(i => i.Key == "pods").Count);
    }

    [Fact]
    public async Task A_watch_event_moves_the_badge_without_anyone_asking()
    {
        // The whole chain, once, through the settle window: stream → reload → callback → badge. The
        // test above pins the wiring and this one pins that something actually pulls it, which is the
        // half that a callback nobody invokes would still pass.
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));
        shell.NavigateCommand.Execute("pods");

        var doomed = new ResourceRef(GroupVersionKind.Pod, "app", "api-7d9c");
        var before = (await cluster.ListPodsAsync()).Count;
        await cluster.DeleteAsync(doomed);

        cluster.EmitWatchEvent(new ResourceEvent { Type = WatchEventType.Deleted, Resource = doomed });

        // Polled to a deadline rather than slept for a fixed span: the settle is 400ms and a test that
        // waits exactly that long is a coin flip on a loaded machine.
        var want = (before - 1).ToString(CultureInfo.InvariantCulture);
        Assert.Equal(want, await EventuallyAsync(() =>
            shell.NavGroups.SelectMany(g => g.Items).Single(i => i.Key == "pods").Count, want));
    }

    /// <summary>Re-read <paramref name="read"/> until it says <paramref name="want"/> or time is up.</summary>
    private static async Task<string> EventuallyAsync(Func<string> read, string want)
    {
        for (var i = 0; i < 60; i++)
        {
            if (read() == want)
                break;

            await Task.Delay(50);
        }

        return read();
    }

    [Fact]
    public async Task An_engine_page_that_sees_a_change_gets_the_badges_refreshed()
    {
        // Same gap on the Docker side, and the same fix: ContainersViewModel was reloading itself on
        // engine events while the badge beside it kept the total it started with.
        var engine = new FakeEngine();
        var shell = new MainWindowViewModel();
        await shell.EnterEngineModeAsync(engine);

        Assert.NotNull(shell.Containers);
        Assert.NotNull(shell.Containers!.Changed);

        var doomed = (await engine.ListImagesAsync())[0];
        var before = (await engine.ListImagesAsync()).Count;
        await engine.RemoveImageAsync(doomed.Id, force: true);

        shell.Containers.Changed!.Invoke();

        Assert.Equal(
            (before - 1).ToString(CultureInfo.InvariantCulture),
            shell.NavGroups.SelectMany(g => g.Items).Single(i => i.Key == "images").Count);
    }
}
