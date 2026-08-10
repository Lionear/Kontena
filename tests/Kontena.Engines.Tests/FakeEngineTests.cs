using System.Text;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Engines.Fakes;
using Xunit;

namespace Kontena.Engines.Tests;

public class FakeEngineTests
{
    private static FakeEngine NewEngine() => new();

    /// <summary>
    /// The migration runner's tests assert what it asked the engine to do, so the fake has to
    /// remember it. A fake that accepts everything and records nothing lets any ordering bug through.
    /// </summary>
    [Fact]
    public async Task CreateContainerAsync_records_the_request_it_was_given()
    {
        var engine = NewEngine();

        await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "alpine:3.20",
            Name = "web",
            Start = false,
            Mounts = [new MountSpec(MountSpec.Volume, "data", "/data")],
        });

        var recorded = Assert.Single(engine.CreatedRequests);
        Assert.Equal("web", recorded.Name);
        Assert.False(recorded.Start);
        Assert.Equal("data", Assert.Single(recorded.Mounts).Source);
    }

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
        Assert.Equal(12, all.Count);

        var running = await NewEngine().ListContainersAsync(all: false);
        Assert.All(running, c => Assert.Equal(ContainerState.Running, c.State));
        Assert.Equal(9, running.Count);
    }

    [Fact]
    public async Task Externally_managed_container_is_recognized_by_labels()
    {
        var all = await NewEngine().ListContainersAsync(all: true);

        var managed = all.Where(c => c.IsManagedExternally).ToList();
        Assert.NotEmpty(managed);
        Assert.All(managed, c => Assert.Equal("sqlexplorer", c.ManagedSource));

        var own = all.First(c => c.Name == "api-gateway");
        Assert.False(own.IsManagedExternally);
    }

    [Fact]
    public async Task Seeded_containers_carry_compose_labels()
    {
        var all = await NewEngine().ListContainersAsync(all: true);

        var projects = all
            .Where(c => c.Labels.ContainsKey("com.docker.compose.project"))
            .Select(c => c.Labels["com.docker.compose.project"])
            .Distinct()
            .ToList();

        Assert.Contains("ashenmoon-stack", projects);
        Assert.Contains("monitoring", projects);
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
    public async Task Build_streams_steps_and_adds_the_image()
    {
        var engine = NewEngine();
        var before = (await engine.ListImagesAsync()).Count;

        var lines = new List<string>();
        await foreach (var p in engine.BuildImageAsync(new BuildRequest { ContextPath = ".", Tag = "myapp:1.0" }))
            lines.Add(p.Text);

        Assert.Contains(lines, l => l.StartsWith("Step 1/", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Successfully tagged myapp:1.0", StringComparison.Ordinal));

        var after = await engine.ListImagesAsync();
        Assert.Equal(before + 1, after.Count);
        Assert.Contains(after, i => i.Repository == "myapp" && i.Tag == "1.0");
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
    public async Task Inspect_image_returns_ports_and_volumes_for_prefill()
    {
        var engine = NewEngine();

        var config = await engine.InspectImageAsync("nginx:latest");

        Assert.NotNull(config);
        Assert.NotEmpty(config!.ExposedPorts);
        Assert.NotEmpty(config.Volumes);
    }

    [Fact]
    public async Task Inspect_image_empty_reference_returns_null()
    {
        var engine = NewEngine();
        Assert.Null(await engine.InspectImageAsync(""));
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
    public async Task Inspect_returns_structured_detail()
    {
        var engine = NewEngine();
        var container = (await engine.ListContainersAsync()).First();

        var inspect = await engine.InspectContainerAsync(container.Id);

        Assert.Equal(container.Id, inspect.Id);
        Assert.Equal(container.Image, inspect.Image);
        Assert.Equal(container.State, inspect.State);
        Assert.NotEmpty(inspect.EnvironmentVariables);
        Assert.NotEmpty(inspect.Networks);
    }

    [Fact]
    public async Task Inspect_missing_container_throws_not_found()
    {
        var engine = NewEngine();
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            async () => await engine.InspectContainerAsync("does-not-exist"));
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

    [Fact]
    public async Task ComposeUp_streams_progress_and_creates_the_project_services()
    {
        var engine = NewEngine();
        var request = new ComposeUpRequest
        {
            ComposeFilePath = "/srv/compose/shop/docker-compose.yml",
            ProjectName = "shop",
        };

        var lines = new List<ComposeProgress>();
        await foreach (var progress in engine.ComposeUpAsync(request))
            lines.Add(progress);

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Null(l.Error));

        // The project's services now exist, grouped by the Compose project label.
        var services = (await engine.ListContainersAsync())
            .Where(c => c.Labels.GetValueOrDefault("com.docker.compose.project") == "shop")
            .ToList();
        Assert.NotEmpty(services);
        Assert.All(services, c => Assert.Equal(ContainerState.Running, c.State));
    }

    [Fact]
    public async Task ComposeUp_defaults_the_project_name_from_the_file_folder()
    {
        var engine = NewEngine();
        var request = new ComposeUpRequest
        {
            ComposeFilePath = "/srv/compose/Storefront/compose.yaml",
        };

        await foreach (var _ in engine.ComposeUpAsync(request)) { }

        var projects = (await engine.ListContainersAsync())
            .Select(c => c.Labels.GetValueOrDefault("com.docker.compose.project"))
            .Distinct()
            .ToList();

        // Derived from the parent folder name, lower-cased ("Storefront" -> "storefront").
        Assert.Contains("storefront", projects);
    }
}
