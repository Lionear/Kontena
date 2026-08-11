using Docker.DotNet.Models;
using Xunit;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// The inspect mapper on its own — no daemon involved, so these run everywhere the suite does.
/// </summary>
public sealed class DockerInspectMappingTests
{
    /// <summary>
    /// <c>Command</c> joins entry point and command for display. Migration re-runs the container, and a
    /// joined line cannot be split back apart once an argument holds a space — so both lists survive
    /// on their own.
    /// </summary>
    [Fact]
    public void MapInspect_keeps_entrypoint_and_cmd_apart()
    {
        var response = new ContainerInspectResponse
        {
            ID = "abc",
            Name = "/web",
            Config = new Config
            {
                Image = "nginx:alpine",
                Entrypoint = ["/docker-entrypoint.sh"],
                Cmd = ["nginx", "-g", "daemon off;"],
                Env = [],
            },
            State = new ContainerState { Status = "running" },
        };

        var inspect = DockerEngine.MapInspect(response);

        Assert.Equal(["/docker-entrypoint.sh"], inspect.Entrypoint);
        Assert.Equal(["nginx", "-g", "daemon off;"], inspect.Cmd);
        Assert.Equal("/docker-entrypoint.sh nginx -g daemon off;", inspect.Command);
    }

    private static ContainerInspectResponse Response(HostConfig hostConfig) => new()
    {
        ID = "abc",
        Name = "/web",
        Config = new Config { Image = "axllent/mailpit", Env = [] },
        State = new ContainerState { Status = "exited" },
        HostConfig = hostConfig,
    };

    /// <summary>
    /// The ports come from <c>HostConfig</c>, which holds what the container was created to publish —
    /// and this response is a <b>stopped</b> container, the case that made this move off the list entry.
    /// Docker reports no ports there for one that is not running (KON-369).
    /// </summary>
    [Fact]
    public void MapInspect_reads_published_ports_of_a_stopped_container()
    {
        var inspect = DockerEngine.MapInspect(Response(new HostConfig
        {
            PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                // Both host sides of one mapping, exactly as Docker reports them.
                ["1025/tcp"] = [new PortBinding { HostIP = "0.0.0.0", HostPort = "25" },
                                new PortBinding { HostIP = "::", HostPort = "25" }],
                ["8025/tcp"] = [new PortBinding { HostPort = "8025" }],
            },
        }));

        Assert.Equal(
            [(25, 1025, "tcp"), (8025, 8025, "tcp")],
            inspect.Ports.Select(p => (p.HostPort, p.ContainerPort, p.Protocol)).Order());
    }

    /// <summary>
    /// A binding with no host port is Docker being asked to pick one. There is nothing to carry over,
    /// and inventing a number would publish somewhere the user never chose.
    /// </summary>
    [Fact]
    public void MapInspect_skips_a_binding_without_a_host_port()
    {
        var inspect = DockerEngine.MapInspect(Response(new HostConfig
        {
            PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                ["80/tcp"] = [new PortBinding { HostPort = string.Empty }],
            },
        }));

        Assert.Empty(inspect.Ports);
    }

    /// <summary>A container that publishes nothing has no ports, not a mapping of empty ones.</summary>
    [Fact]
    public void MapInspect_reports_no_ports_when_nothing_is_published()
    {
        Assert.Empty(DockerEngine.MapInspect(Response(new HostConfig())).Ports);
    }
}
