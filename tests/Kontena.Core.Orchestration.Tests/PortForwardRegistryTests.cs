using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Models;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The registry is what makes a forward outlive the dialog that started it, so what matters is that it
/// keeps tunnels until something explicitly stops them — and that stopping actually tears them down.
/// </summary>
public class PortForwardRegistryTests
{
    private static readonly ResourceRef Api = new(GroupVersionKind.Service, "app", "api");
    private static readonly ResourceRef Web = new(GroupVersionKind.Pod, "app", "web-0");

    private static (PortForwardRegistry Registry, FakeClusterEngine Cluster) New() => (new(), new());

    [Fact]
    public async Task A_started_forward_is_kept_and_described()
    {
        var (registry, cluster) = New();

        var entry = await registry.StartAsync(cluster, Api, "api · app", remotePort: 80, localPort: 8080);

        Assert.Same(entry, Assert.Single(registry.Forwards));
        Assert.Equal(1, registry.Count);
        Assert.Equal("localhost:8080", entry.Address);
        Assert.Equal("Service", entry.TargetKind);
        Assert.True(entry.IsActive);
    }

    [Fact]
    public async Task A_pod_forward_is_labelled_as_a_pod()
    {
        var (registry, cluster) = New();

        var entry = await registry.StartAsync(cluster, Web, "web-0 · app", 8080, 8080);

        Assert.Equal("Pod", entry.TargetKind);
    }

    [Fact]
    public async Task Stopping_removes_the_forward_and_tears_the_tunnel_down()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        await registry.StopAsync(entry);

        Assert.Empty(registry.Forwards);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public async Task Stopping_twice_is_harmless()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        await registry.StopAsync(entry);
        await registry.StopAsync(entry);

        Assert.Empty(registry.Forwards);
    }

    [Fact]
    public async Task Switching_backend_stops_everything()
    {
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        await registry.StartAsync(cluster, Web, "web-0 · app", 8080, 8081);

        await registry.StopAllAsync();

        Assert.Empty(registry.Forwards);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task The_registry_reports_which_forward_holds_a_local_port()
    {
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        Assert.Equal("api · app", registry.OnLocalPort(8080)?.TargetLabel);
        Assert.Null(registry.OnLocalPort(9999));
    }

    [Fact]
    public async Task Changed_fires_on_start_and_on_stop_so_the_sidebar_badge_can_follow()
    {
        var (registry, cluster) = New();
        var fired = 0;
        registry.Changed += () => fired++;

        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        await registry.StopAsync(entry);

        Assert.Equal(2, fired);
    }

    // ── A tunnel that falls over on its own (KON-102) ────────────────────────

    [Fact]
    public async Task A_dropped_tunnel_says_so_without_being_asked()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        var changes = new List<string?>();
        entry.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        cluster.LastPortForward!.Drop("The pod is gone.");

        Assert.False(entry.IsActive);
        Assert.Equal("The pod is gone.", entry.DropReason);
        Assert.Contains(nameof(ActivePortForward.IsActive), changes);
        Assert.Contains(nameof(ActivePortForward.DropReason), changes);
    }

    [Fact]
    public async Task A_dropped_tunnel_stays_on_the_list()
    {
        // Removing it would take the local port away silently, which is worse than a row that
        // reports itself as dropped.
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        cluster.LastPortForward!.Drop();

        Assert.Single(registry.Forwards);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task A_dropped_tunnel_stops_counting_towards_the_badge()
    {
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        await registry.StartAsync(cluster, Web, "web-0 · app", 8080, 8081);
        var fired = 0;
        registry.Changed += () => fired++;

        cluster.LastPortForward!.Drop();

        Assert.Equal(2, registry.Count);
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Reconnecting_reopens_the_tunnel_on_the_same_local_port()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        cluster.LastPortForward!.Drop();

        await registry.ReconnectAsync(entry);

        Assert.True(entry.IsActive);
        Assert.Null(entry.DropReason);
        Assert.Equal(8080, entry.LocalPort);
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public async Task A_reconnected_tunnel_can_drop_again()
    {
        // The replacement handle has to be listened to as well; forgetting that would leave the row
        // stuck on Active for the rest of the session.
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        cluster.LastPortForward!.Drop();
        await registry.ReconnectAsync(entry);

        cluster.LastPortForward!.Drop("Gone again.");

        Assert.False(entry.IsActive);
        Assert.Equal("Gone again.", entry.DropReason);
    }

    [Fact]
    public async Task Reconnecting_a_live_tunnel_does_nothing()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        var first = cluster.LastPortForward;

        await registry.ReconnectAsync(entry);

        Assert.Same(first, cluster.LastPortForward);
        Assert.True(entry.IsActive);
    }

    // ── Pausing: hand the port back, keep the row ───────────────────────────

    [Fact]
    public async Task Pausing_closes_the_tunnel_but_keeps_the_row()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        await registry.PauseAsync(entry);

        Assert.Same(entry, Assert.Single(registry.Forwards));
        Assert.Equal(PortForwardState.Paused, entry.State);
        Assert.False(entry.IsActive);
        Assert.Equal(0, registry.ActiveCount);
        Assert.False(cluster.LastPortForward!.IsActive);
    }

    [Fact]
    public async Task Pausing_is_not_reported_as_a_drop()
    {
        // Tearing it down ourselves is not the tunnel dying, and must not read as one.
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        await registry.PauseAsync(entry);

        Assert.Null(entry.DropReason);
        Assert.Equal("Resume", entry.ReopenLabel);
    }

    [Fact]
    public async Task A_paused_forward_resumes_on_the_same_local_port()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        await registry.PauseAsync(entry);

        await registry.ReconnectAsync(entry);

        Assert.True(entry.IsActive);
        Assert.Equal(PortForwardState.Active, entry.State);
        Assert.Equal(8080, entry.LocalPort);
    }

    [Fact]
    public async Task A_paused_forward_is_remembered_like_any_other()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        await registry.PauseAsync(entry);

        Assert.Single(registry.Snapshot());
        Assert.True(registry.HasReopenable);
    }

    [Fact]
    public async Task Pausing_a_forward_that_is_not_running_does_nothing()
    {
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        cluster.LastPortForward!.Drop("Gone.");

        await registry.PauseAsync(entry);

        // A dropped tunnel stays dropped, with its reason — pausing it would hide why it ended.
        Assert.Equal(PortForwardState.Dropped, entry.State);
        Assert.Equal("Gone.", entry.DropReason);
    }

    // ── Remembered between sessions (KON-105) ───────────────────────────────

    [Fact]
    public async Task What_is_on_the_list_is_what_gets_remembered()
    {
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);
        var stopped = await registry.StartAsync(cluster, Web, "web-0 · app", 8080, 8081);
        await registry.StopAsync(stopped);

        var snapshot = registry.Snapshot();

        // Stopping one is how you say you are done with it; it must not come back next session.
        var only = Assert.Single(snapshot);
        Assert.Equal("api", only.Name);
        Assert.Equal("Service", only.Kind);
        Assert.Equal("app", only.Namespace);
        Assert.Equal(80, only.RemotePort);
        Assert.Equal(8080, only.LocalPort);
    }

    [Fact]
    public async Task A_dropped_forward_is_still_remembered()
    {
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        cluster.LastPortForward!.Drop();

        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void A_restored_forward_is_not_opened_by_itself()
    {
        // Opening a tunnel into production because the app started is a surprise nobody asked for.
        var (registry, cluster) = New();

        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080)]);

        var entry = Assert.Single(registry.Forwards);
        Assert.Equal(PortForwardState.Remembered, entry.State);
        Assert.False(entry.IsActive);
        Assert.Equal(0, registry.ActiveCount);
        Assert.Null(cluster.LastPortForward);
        Assert.Equal(8080, entry.LocalPort);
        Assert.Equal("api · app", entry.TargetLabel);
    }

    [Fact]
    public async Task A_restored_forward_opens_on_the_port_it_had()
    {
        var (registry, cluster) = New();
        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080)]);
        var entry = registry.Forwards[0];

        await registry.ReconnectAsync(entry);

        Assert.True(entry.IsActive);
        Assert.Equal(PortForwardState.Active, entry.State);
        Assert.Equal(8080, entry.LocalPort);
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public async Task A_restored_forward_that_is_opened_can_drop_like_any_other()
    {
        var (registry, cluster) = New();
        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080)]);
        await registry.ReconnectAsync(registry.Forwards[0]);

        cluster.LastPortForward!.Drop("Gone.");

        Assert.Equal(PortForwardState.Dropped, registry.Forwards[0].State);
        Assert.Equal("Gone.", registry.Forwards[0].DropReason);
    }

    [Fact]
    public async Task Restoring_does_not_duplicate_a_forward_that_is_already_open()
    {
        // Restore runs on every activation of the cluster; a second pass must not add a second row
        // for a port that is already served.
        var (registry, cluster) = New();
        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080)]);

        Assert.Single(registry.Forwards);
        Assert.True(registry.Forwards[0].IsActive);
    }

    [Fact]
    public async Task Starting_a_forward_replaces_the_remembered_row_for_that_port()
    {
        var (registry, cluster) = New();
        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080)]);

        await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        var entry = Assert.Single(registry.Forwards);
        Assert.True(entry.IsActive);
    }

    [Fact]
    public void Anything_not_running_can_be_reopened()
    {
        var (registry, cluster) = New();
        Assert.False(registry.HasReopenable);

        registry.Restore(cluster, [new RememberedPortForward("", "v1", "Pod", "app", "web-0", "web-0 · app", 8080, 9229)]);

        Assert.True(registry.HasReopenable);
    }

    [Fact]
    public async Task Stopping_a_forward_is_not_reported_as_a_drop()
    {
        // Disposal is the user's own doing; the row is gone, and a "the tunnel died" notification
        // after it would be noise at best.
        var (registry, cluster) = New();
        var entry = await registry.StartAsync(cluster, Api, "api · app", 80, 8080);

        await registry.StopAsync(entry);

        Assert.Null(entry.DropReason);
    }
}
