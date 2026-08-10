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
}
