using System.Text;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Xunit;

namespace Kontena.Engines.Tests;

public class FakeEngineTests
{
    private static FakeEngine NewEngine() => new();

    [Fact]
    public void Capabilities_advertise_expected_flags()
    {
        var caps = NewEngine().Capabilities;
        Assert.True(caps.SupportsStats);
        Assert.True(caps.SupportsEvents);
        Assert.True(caps.SupportsExec);
        Assert.False(caps.SupportsGpu);
    }

    [Fact]
    public async Task Lists_seeded_containers()
    {
        var all = await NewEngine().ListContainersAsync(all: true);
        Assert.Equal(5, all.Count);

        var running = await NewEngine().ListContainersAsync(all: false);
        Assert.All(running, c => Assert.Equal(ContainerState.Running, c.State));
        Assert.Equal(3, running.Count);
    }

    [Fact]
    public async Task Lifecycle_stop_then_start_transitions_state()
    {
        var engine = NewEngine();
        var target = (await engine.ListContainersAsync()).First(c => c.State == ContainerState.Running);

        await engine.StopContainerAsync(target.Id);
        var afterStop = (await engine.ListContainersAsync()).Single(c => c.Id == target.Id);
        Assert.Equal(ContainerState.Exited, afterStop.State);

        await engine.StartContainerAsync(target.Id);
        var afterStart = (await engine.ListContainersAsync()).Single(c => c.Id == target.Id);
        Assert.Equal(ContainerState.Running, afterStart.State);
    }

    [Fact]
    public async Task Create_then_remove_container()
    {
        var engine = NewEngine();
        var id = await engine.CreateContainerAsync(new CreateContainerRequest { Image = "nginx:latest" });

        var created = (await engine.ListContainersAsync()).Single(c => c.Id == id);
        Assert.Equal(ContainerState.Running, created.State);

        await engine.RemoveContainerAsync(id, force: true);
        Assert.DoesNotContain(await engine.ListContainersAsync(), c => c.Id == id);
    }

    [Fact]
    public async Task Pull_image_reports_progress_and_adds_image()
    {
        var engine = NewEngine();
        var before = (await engine.ListImagesAsync()).Count;

        var updates = new List<PullProgress>();
        await foreach (var p in engine.PullImageAsync("alpine:3.20"))
            updates.Add(p);

        Assert.NotEmpty(updates);
        Assert.Equal("Pull complete", updates[^1].Status);

        var after = await engine.ListImagesAsync();
        Assert.Equal(before + 1, after.Count);
        Assert.Contains(after, i => i.Repository == "alpine" && i.Tag == "3.20");
    }

    [Fact]
    public async Task Streams_logs_yield_lines()
    {
        var engine = NewEngine();
        var container = (await engine.ListContainersAsync()).First();

        var count = 0;
        await foreach (var _ in engine.StreamLogsAsync(container.Id, follow: false))
            count++;

        Assert.True(count > 0);
    }

    [Fact]
    public async Task Streams_stats_yield_samples_for_the_container()
    {
        var engine = NewEngine();
        var container = (await engine.ListContainersAsync()).First();

        var samples = new List<ContainerStats>();
        await foreach (var s in engine.StreamStatsAsync(container.Id))
            samples.Add(s);

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(container.Id, s.ContainerId));
    }

    [Fact]
    public async Task Prune_removes_unused_images_and_reports_reclaim()
    {
        var engine = NewEngine();
        var unusedBefore = (await engine.ListImagesAsync()).Count(i => !i.InUse);

        var result = await engine.PruneImagesAsync();

        Assert.Equal(unusedBefore, result.ItemsDeleted);
        Assert.True(result.SpaceReclaimedBytes > 0);

        var after = await engine.ListImagesAsync();
        Assert.All(after, i => Assert.True(i.InUse));
    }

    [Fact]
    public async Task Prune_containers_removes_stopped_ones()
    {
        var engine = NewEngine();
        var stoppedBefore = (await engine.ListContainersAsync())
            .Count(c => c.State is ContainerState.Exited or ContainerState.Created or ContainerState.Dead);

        var result = await engine.PruneContainersAsync();

        Assert.Equal(stoppedBefore, result.ItemsDeleted);
        Assert.DoesNotContain(await engine.ListContainersAsync(),
            c => c.State is ContainerState.Exited or ContainerState.Created or ContainerState.Dead);
    }

    [Fact]
    public async Task Prune_volumes_removes_dangling_ones()
    {
        var engine = NewEngine();
        var danglingBefore = (await engine.ListVolumesAsync()).Count(v => v.IsDangling);

        var result = await engine.PruneVolumesAsync();

        Assert.Equal(danglingBefore, result.ItemsDeleted);
        Assert.DoesNotContain(await engine.ListVolumesAsync(), v => v.IsDangling);
    }

    [Fact]
    public async Task Removing_missing_container_throws_not_found()
    {
        var engine = NewEngine();
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            async () => await engine.RemoveContainerAsync("does-not-exist"));
    }

    [Fact]
    public async Task Removing_builtin_network_is_rejected()
    {
        var engine = NewEngine();
        var builtIn = (await engine.ListNetworksAsync()).First(n => n.IsBuiltIn);
        await Assert.ThrowsAsync<EngineException>(
            async () => await engine.RemoveNetworkAsync(builtIn.Id));
    }

    [Fact]
    public async Task Exec_session_echoes_input_and_exits_on_exit()
    {
        var engine = NewEngine();
        var container = (await engine.ListContainersAsync()).First();

        await using var session = await engine.StartExecSessionAsync(
            container.Id, new ExecRequest { Command = ["/bin/sh"], Tty = true });

        Assert.Null(session.ExitCode);

        // Unbounded channel: writes buffer, and 'exit' completes the stream, so a
        // straight drain afterwards sees everything and then ends.
        await session.WriteAsync(Encoding.UTF8.GetBytes("hello\r"));
        await session.WriteAsync(Encoding.UTF8.GetBytes("exit\r"));

        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            output.Append(Encoding.UTF8.GetString(chunk.Span));

        Assert.Contains("hello", output.ToString());
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task Exec_session_for_missing_container_throws_not_found()
    {
        var engine = NewEngine();
        await Assert.ThrowsAsync<ResourceNotFoundException>(async () =>
            await engine.StartExecSessionAsync("does-not-exist", new ExecRequest { Command = ["/bin/sh"] }));
    }

    [Fact]
    public async Task Cancelling_a_stream_stops_enumeration()
    {
        var engine = NewEngine();
        var container = (await engine.ListContainersAsync()).First();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in engine.StreamStatsAsync(container.Id, cts.Token))
                cts.Cancel();
        });
    }
}
