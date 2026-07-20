using Kontena.Adapters.Docker;
using Kontena.Core.Models;
using Xunit;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// Integration tests against a real local Docker engine. Each test skips
/// cleanly when Docker is not reachable (e.g. on CI runners without Docker),
/// so the suite is green everywhere and meaningful where Docker runs.
/// </summary>
public class DockerEngineTests
{
    private static async Task<DockerEngine> ConnectOrSkipAsync()
    {
        var engine = new DockerEngine();
        var reachable = true;
        try
        {
            await engine.PingAsync();
        }
        catch
        {
            reachable = false;
        }

        if (!reachable)
        {
            engine.Dispose();
            Skip.If(true, "Docker engine is not reachable on this host.");
        }

        return engine;
    }

    [SkippableFact]
    public async Task Ping_succeeds_when_docker_is_up()
    {
        using var engine = await ConnectOrSkipAsync();
        await engine.PingAsync(); // must not throw
    }

    [SkippableFact]
    public async Task GetInfo_reports_docker_and_a_version()
    {
        using var engine = await ConnectOrSkipAsync();
        var info = await engine.GetInfoAsync();

        Assert.Equal("docker", info.Backend);
        Assert.Equal("Docker", info.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Equal(EngineConnectionState.Connected, info.ConnectionState);
    }

    [SkippableFact]
    public async Task Lists_containers_as_neutral_models()
    {
        using var engine = await ConnectOrSkipAsync();
        var containers = await engine.ListContainersAsync(all: true);

        // Every mapped container carries the docker backend and a non-empty id/name.
        Assert.All(containers, c =>
        {
            Assert.Equal("docker", c.Backend);
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
    }

    [SkippableFact]
    public async Task Lists_images_volumes_and_networks()
    {
        using var engine = await ConnectOrSkipAsync();

        var images = await engine.ListImagesAsync();
        var volumes = await engine.ListVolumesAsync();
        var networks = await engine.ListNetworksAsync();

        Assert.NotNull(images);
        Assert.NotNull(volumes);
        // Docker always has the built-in bridge/host/none networks.
        Assert.Contains(networks, n => n.Name is "bridge" or "host" or "none");
        Assert.Contains(networks, n => n.IsBuiltIn);
    }

    [SkippableFact]
    public async Task Run_lifecycle_and_remove_a_throwaway_container()
    {
        using var engine = await ConnectOrSkipAsync();

        // hello-world is tiny; pull-if-missing is handled by the adapter.
        var id = await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "hello-world:latest",
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
            Start = true,
        });

        try
        {
            var listed = await engine.ListContainersAsync(all: true);
            Assert.Contains(listed, c => c.Id == id);
        }
        finally
        {
            await engine.RemoveContainerAsync(id, force: true);
        }

        var after = await engine.ListContainersAsync(all: true);
        Assert.DoesNotContain(after, c => c.Id == id);
    }

    [SkippableFact]
    public async Task Streams_logs_for_a_hello_world_run()
    {
        using var engine = await ConnectOrSkipAsync();

        var id = await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "hello-world:latest",
            Name = $"kontena-logs-{Guid.NewGuid():N}"[..24],
            Start = true,
        });

        try
        {
            var lines = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await foreach (var _ in engine.StreamLogsAsync(id, follow: false, cts.Token))
            {
                if (++lines >= 1) break;
            }

            Assert.True(lines >= 1, "expected at least one log line from hello-world");
        }
        finally
        {
            await engine.RemoveContainerAsync(id, force: true);
        }
    }
}
