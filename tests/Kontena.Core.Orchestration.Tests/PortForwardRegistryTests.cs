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
        entry.Refresh();
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
}
