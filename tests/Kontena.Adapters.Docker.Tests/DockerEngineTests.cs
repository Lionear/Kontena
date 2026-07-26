using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using ContainerState = Kontena.Core.Models.ContainerState;
using Kontena.Adapters.Docker;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Engines;
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


    /// <summary>
    /// Runs a shell line against a fresh container with <paramref name="volume"/> mounted at /v, waits
    /// for it to finish, and removes it. Test scaffolding, not part of the adapter's surface.
    /// </summary>
    private static async Task WriteIntoVolumeAsync(string volume, string shell)
    {
        using var client = new DockerClientConfiguration().CreateClient();

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
            null,
            new Progress<JSONMessage>());

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "busybox:latest",
            Cmd = ["sh", "-c", shell],
            HostConfig = new HostConfig { Binds = [$"{volume}:/v"] },
        });

        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters());
            await client.Containers.WaitContainerAsync(created.ID);
        }
        finally
        {
            await client.Containers.RemoveContainerAsync(
                created.ID, new ContainerRemoveParameters { Force = true });
        }
    }

    [SkippableFact]
    public async Task A_container_can_be_attached_to_a_network_and_taken_off_again()
    {
        // The point of a user-defined network is containers reaching each other by name, and that only
        // works once something is actually on it. Asserted against the engine's own view of the network,
        // not against the call returning quietly.
        using var engine = await ConnectOrSkipAsync();

        var network = await engine.CreateNetworkAsync(new CreateNetworkRequest
        {
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
        });

        // Running, because Docker only counts a container as attached while it is up: connecting a stopped
        // one is allowed (see the next test) but does not show until it starts.
        var container = await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "busybox:latest",
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
            Start = false,
        });

        try
        {
            await engine.ConnectNetworkAsync(container, network.Id);
            await engine.StartContainerAsync(container);

            var attached = Assert.Single(await engine.ListNetworksAsync(), n => n.Id == network.Id);
            Assert.NotEmpty(attached.AttachedContainers);

            await engine.DisconnectNetworkAsync(container, network.Id, force: true);

            var detached = Assert.Single(await engine.ListNetworksAsync(), n => n.Id == network.Id);
            Assert.Empty(detached.AttachedContainers);
        }
        finally
        {
            await engine.RemoveContainerAsync(container, force: true);
            await engine.RemoveNetworkAsync(network.Id);
        }
    }

    [SkippableFact]
    public async Task Attaching_a_stopped_container_is_allowed()
    {
        // It takes effect when the container next starts. Treating this as an error would mean telling
        // someone to start a container before they can wire it up, which is backwards.
        using var engine = await ConnectOrSkipAsync();

        var network = await engine.CreateNetworkAsync(new CreateNetworkRequest
        {
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
        });
        var container = await engine.CreateContainerAsync(new CreateContainerRequest
        {
            Image = "busybox:latest",
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
            Start = false,
        });

        try
        {
            await engine.ConnectNetworkAsync(container, network.Id);
        }
        finally
        {
            await engine.RemoveContainerAsync(container, force: true);
            await engine.RemoveNetworkAsync(network.Id);
        }
    }

    [SkippableFact]
    public async Task Browsing_a_volume_lists_what_a_container_wrote_into_it()
    {
        // The whole feature rests on a claim that cannot be unit-tested: that a volume's contents are
        // readable from a container that was created but never started. This asserts exactly that,
        // against a real engine, by writing files with one container and listing them without running
        // anything at all.
        using var engine = await ConnectOrSkipAsync();

        var volume = await engine.CreateVolumeAsync(new CreateVolumeRequest
        {
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
        });

        try
        {
            // Arranged with the raw client rather than through CEAL: seeding a volume needs a command,
            // which CreateContainerRequest deliberately does not carry. The assertion below is the
            // CEAL call — that is what is under test.
            await WriteIntoVolumeAsync(
                volume.Name,
                "mkdir -p /v/nested && echo hello > /v/top.txt && echo deep > /v/nested/inner.txt");

            var root = await engine.BrowseVolumeAsync(volume.Name);

            Assert.Equal("/", root.Path);
            Assert.False(root.Truncated);
            Assert.Contains(root.Entries, e => e.Name == "top.txt" && !e.IsDirectory && e.SizeBytes > 0);
            Assert.Contains(root.Entries, e => e.Name == "nested" && e.IsDirectory);

            // One level down, and only that level: inner.txt is a child of nested, not of the root.
            Assert.DoesNotContain(root.Entries, e => e.Name == "inner.txt");

            var nested = await engine.BrowseVolumeAsync(volume.Name, "/nested");
            Assert.Equal("/nested", nested.Path);
            Assert.Contains(nested.Entries, e => e.Name == "inner.txt" && !e.IsDirectory);

            // Directories first, then alphabetical — the order the browser relies on.
            Assert.Equal(
                root.Entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Name),
                root.Entries.Select(e => e.Name));
        }
        finally
        {
            await engine.RemoveVolumeAsync(volume.Name, force: true);
        }
    }

    [SkippableFact]
    public async Task Browsing_leaves_no_container_behind()
    {
        using var engine = await ConnectOrSkipAsync();

        var volume = await engine.CreateVolumeAsync(new CreateVolumeRequest
        {
            Name = $"kontena-test-{Guid.NewGuid():N}"[..24],
        });

        try
        {
            var before = (await engine.ListContainersAsync(all: true)).Count;
            await engine.BrowseVolumeAsync(volume.Name);
            var after = (await engine.ListContainersAsync(all: true)).Count;

            // The holder container is an implementation detail; finding it in your list afterwards
            // would make browsing a volume something that litters.
            Assert.Equal(before, after);
        }
        finally
        {
            await engine.RemoveVolumeAsync(volume.Name, force: true);
        }
    }

    [SkippableFact]
    public async Task A_network_created_with_a_subnet_really_has_that_subnet()
    {
        // The adapter used to send only name and driver, then return a summary echoing the subnet from
        // the request — so the network reported a subnet Docker had never been told about, while
        // actually using one from its own pool. This asserts against the engine, not the echo.
        using var engine = await ConnectOrSkipAsync();

        var name = $"kontena-test-{Guid.NewGuid():N}"[..24];
        const string subnet = "172.28.240.0/24";

        var created = await engine.CreateNetworkAsync(new CreateNetworkRequest
        {
            Name = name,
            Driver = "bridge",
            Subnet = subnet,
        });

        try
        {
            Assert.Equal(subnet, created.Subnet);

            // And again from a fresh listing, so it is the engine's answer rather than the create call's.
            var listed = Assert.Single(await engine.ListNetworksAsync(), n => n.Name == name);
            Assert.Equal(subnet, listed.Subnet);
            Assert.Equal("bridge", listed.Driver);
        }
        finally
        {
            await engine.RemoveNetworkAsync(created.Id);
        }
    }

    [SkippableFact]
    public async Task A_network_created_without_a_subnet_reports_the_one_the_engine_chose()
    {
        using var engine = await ConnectOrSkipAsync();

        var name = $"kontena-test-{Guid.NewGuid():N}"[..24];
        var created = await engine.CreateNetworkAsync(new CreateNetworkRequest { Name = name });

        try
        {
            // Not empty: leaving the field blank means "engine decides", and the summary should then
            // say what it decided rather than repeating our blank.
            Assert.False(string.IsNullOrWhiteSpace(created.Subnet));
        }
        finally
        {
            await engine.RemoveNetworkAsync(created.Id);
        }
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

    [SkippableFact]
    public async Task Inspect_image_returns_config_for_present_and_null_for_missing()
    {
        using var engine = await ConnectOrSkipAsync();

        var img = (await engine.ListImagesAsync()).FirstOrDefault();
        Skip.If(img is null, "No images to inspect on this host.");

        var present = await engine.InspectImageAsync($"{img!.Repository}:{img.Tag}");
        Assert.NotNull(present);

        var missing = await engine.InspectImageAsync("kontena/definitely-not-real:0");
        Assert.Null(missing);
    }

    [SkippableFact]
    public async Task Inspect_returns_structured_config_for_a_container()
    {
        using var engine = await ConnectOrSkipAsync();

        var any = (await engine.ListContainersAsync(all: true)).FirstOrDefault();
        Skip.If(any is null, "No container to inspect on this host.");

        var inspect = await engine.InspectContainerAsync(any!.Id);

        Assert.Equal(any.Id, inspect.Id);
        Assert.False(string.IsNullOrWhiteSpace(inspect.Name));
        Assert.False(string.IsNullOrWhiteSpace(inspect.Image));
    }

    [SkippableFact]
    public async Task Exec_session_runs_a_command_in_a_running_container()
    {
        using var engine = await ConnectOrSkipAsync();

        var running = (await engine.ListContainersAsync(all: false))
            .FirstOrDefault(c => c.State == ContainerState.Running);
        Skip.If(running is null, "No running container to exec into on this host.");

        IExecSession session;
        try
        {
            session = await engine.StartExecSessionAsync(running!.Id,
                new ExecRequest { Command = ["/bin/sh", "-c", "echo kontena-exec-ok"] });
        }
        catch (EngineException)
        {
            Skip.If(true, "Selected container has no /bin/sh to exec.");
            return;
        }

        await using (session)
        {
            var output = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await foreach (var chunk in session.ReadOutputAsync(cts.Token))
                {
                    output.Append(Encoding.UTF8.GetString(chunk.Span));
                    if (output.ToString().Contains("kontena-exec-ok", StringComparison.Ordinal))
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // fall through to the assertion, which will report the timeout
            }

            Assert.Contains("kontena-exec-ok", output.ToString());
        }
    }
}
