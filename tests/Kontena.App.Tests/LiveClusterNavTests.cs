using System.Globalization;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The sidebar follows the streams the lists already follow (KON-339).
/// <para>
/// The lists went live in KON-250 and the sidebar beside them did not, so a namespace that appeared
/// while you watched showed up in the list without ever reaching the picker that filters it. The page
/// knew; nothing carried it to the shell.
/// </para>
/// <para>
/// This used to be checked on the per-entry badges, which is what the refresh mainly existed for.
/// Those cost twelve cluster-wide list calls a round and are gone (KON-354); the refresh itself stayed,
/// because the picker and the Workloads submenu still have to follow the cluster.
/// </para>
/// <para>
/// Driven by invoking the callback rather than by pushing a watch event and waiting out the 400ms
/// settle: what broke was the wiring between page and shell, and a test that sleeps to observe it
/// would be testing <c>Task.Delay</c>. That the settle fires the callback is the line right under it.
/// </para>
/// </summary>
public sealed class LiveClusterNavTests
{
    /// <summary>
    /// The rule is that no entry counts <i>the resources it lists</i> (KON-354) — including the
    /// per-kind Workloads entries, which kept theirs at first because the number was already in hand
    /// and then stood out as the only badges left.
    /// <para>
    /// Two entries carry a number that is not an inventory, and both are listed here rather than
    /// skipped silently. <c>portforwards</c> says how many tunnels <i>you</i> have running.
    /// <c>alerts</c> says how many things are wrong and unmuted (KON-207) — a queue rather than a
    /// count of what the page holds, which is also why it is the one badge allowed to be loud.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_entry_in_the_cluster_sidebar_counts_the_resources_it_lists()
    {
        var notAnInventory = new[] { "portforwards", "alerts" };

        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        var badged = shell.NavGroups
            .SelectMany(g => g.Items)
            .Where(i => i.Count.Length > 0 && !notAnInventory.Contains(i.Key, StringComparer.Ordinal))
            .Select(i => i.Key);

        Assert.Empty(badged);
    }

    /// <summary>
    /// And that the alerts badge counts what it claims to: firing and unmuted, which on the seeded
    /// fake is four of its six alerts. A badge that counted pending or silenced ones would be a
    /// number people learn to ignore.
    /// </summary>
    [Fact]
    public async Task The_alerts_badge_counts_firing_and_unmuted_and_shouts_about_it()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        var alerts = Assert.Single(shell.NavGroups.SelectMany(g => g.Items), i => i.Key == "alerts");

        Assert.Equal("4", alerts.Count);
        Assert.True(alerts.IsLoud);
        Assert.Contains("firing", alerts.AttentionTip, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no Alertmanager the entry stays — the empty state is the answer to "where are my alerts"
    /// and hiding the entry makes it unreachable — but it says nothing, because an all-clear is not
    /// news and neither is an absent source.
    /// </summary>
    [Fact]
    public async Task Without_an_Alertmanager_the_entry_is_still_reachable_and_carries_no_badge()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine { HasAlertmanager = false }));

        var alerts = Assert.Single(shell.NavGroups.SelectMany(g => g.Items), i => i.Key == "alerts");

        Assert.Empty(alerts.Count);
        Assert.False(alerts.IsLoud);
    }

    [Fact]
    public async Task A_cluster_page_that_sees_a_change_gets_the_sidebar_refreshed()
    {
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));
        shell.NavigateCommand.Execute("pods");

        var page = Assert.IsAssignableFrom<IClusterLivePage>(shell.CurrentPage);
        Assert.NotNull(page.Changed);

        // Behind the app's back, the way kubectl would.
        Assert.Contains("monitoring", shell.Namespaces);
        await cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Namespace, null, "monitoring"));

        page.Changed!.Invoke();
        await Task.Yield();

        Assert.DoesNotContain("monitoring", shell.Namespaces);
    }

    [Fact]
    public async Task A_watch_event_moves_the_sidebar_without_anyone_asking()
    {
        // The whole chain, once, through the settle window: stream → reload → callback → sidebar. The
        // test above pins the wiring and this one pins that something actually pulls it, which is the
        // half that a callback nobody invokes would still pass.
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));
        shell.NavigateCommand.Execute("pods");

        var doomed = new ResourceRef(GroupVersionKind.Namespace, null, "monitoring");
        Assert.Contains("monitoring", shell.Namespaces);
        await cluster.DeleteAsync(doomed);

        cluster.EmitWatchEvent(new ResourceEvent { Type = WatchEventType.Deleted, Resource = doomed });

        // Polled to a deadline rather than slept for a fixed span: the settle is 400ms and a test that
        // waits exactly that long is a coin flip on a loaded machine.
        Assert.DoesNotContain("monitoring", await EventuallyAsync(() => shell.Namespaces));
    }

    [Fact]
    public async Task Landing_on_a_page_that_watches_nothing_still_refreshes_the_sidebar()
    {
        // Found by driving the real app (KON-339): only the open page's stream feeds the callback, so
        // the sidebar froze the moment you navigated to the Workloads dashboard, Config maps or Events.
        //
        // All of those follow the cluster themselves since KON-340, so Resources stands in: it
        // browses whichever kind you pick, including custom ones the adapter has no watcher for, and
        // so it is the page that genuinely cannot follow anything. This assertion is about the shell,
        // not about which page is on screen.
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));

        Assert.Contains("monitoring", shell.Namespaces);
        await cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Namespace, null, "monitoring"));

        // Resources watches nothing, so nothing here can be the page's own doing.
        shell.NavigateCommand.Execute("resources");
        Assert.IsNotAssignableFrom<IClusterLivePage>(shell.CurrentPage);
        await Task.Yield();

        Assert.DoesNotContain("monitoring", shell.Namespaces);
    }

    [Fact]
    public async Task A_namespace_created_after_the_cluster_opened_can_be_picked()
    {
        // Found by driving the real app (KON-343): the picker was filled once when the cluster
        // opened, so a namespace created afterwards had no way of being selected — while the list
        // beside it was already showing that namespace's contents. Two pieces of UI over one
        // cluster, disagreeing, which is the shape KON-339 was about too.
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));
        Assert.DoesNotContain("payments", shell.Namespaces);

        await foreach (var _ in cluster.ApplyAsync(new ManifestBundle
        {
            Yaml = "apiVersion: v1\nkind: Namespace\nmetadata:\n  name: payments\n",
        }))
        {
            // Drained for its effect on the cluster; the apply's own progress is not what is on trial.
        }

        // The same refresh the badges ride on, which is why this costs no call of its own.
        shell.NavigateCommand.Execute("pods");
        await Task.Yield();

        Assert.Contains("payments", shell.Namespaces);
    }

    [Fact]
    public async Task A_namespace_that_disappears_takes_the_filter_with_it()
    {
        // Reconciling the picker means the selection can be deleted out from under it. Left alone,
        // the ComboBox shows nothing while every list stays filtered to a namespace that is gone —
        // an empty screen with no visible reason for being empty.
        var cluster = new FakeClusterEngine();
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(cluster));

        shell.SelectedNamespace = "monitoring";
        Assert.Contains("monitoring", shell.Namespaces);

        await cluster.DeleteAsync(new ResourceRef(GroupVersionKind.Namespace, null, "monitoring"));
        shell.NavigateCommand.Execute("pods");
        await Task.Yield();

        Assert.DoesNotContain("monitoring", shell.Namespaces);
        Assert.Equal("All namespaces", shell.SelectedNamespace);
    }

    /// <summary>Re-read <paramref name="read"/> until it no longer holds "monitoring", or time is up.</summary>
    private static async Task<IReadOnlyList<string>> EventuallyAsync(Func<IEnumerable<string>> read)
    {
        for (var i = 0; i < 60; i++)
        {
            if (!read().Contains("monitoring"))
                break;

            await Task.Delay(50);
        }

        return [.. read()];
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
