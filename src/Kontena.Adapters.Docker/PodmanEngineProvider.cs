using Kontena.Engines;

namespace Kontena.Adapters.Docker;

/// <summary>
/// Built-in provider for Podman. Podman exposes a Docker-compatible API socket, so it
/// reuses <see cref="DockerEngine"/> pointed at the Podman socket with a Podman identity.
/// </summary>
public sealed class PodmanEngineProvider : IEngineProvider
{
    public string Backend => "podman";
    public string DisplayName => "Podman";
    public string Chip => "P";

    public IContainerEngine CreateEngine() => new DockerEngine(PodmanEndpoint(), "podman", "Podman");

    private static Uri PodmanEndpoint()
    {
        if (OperatingSystem.IsWindows())
            return new Uri("npipe://./pipe/podman-machine-default");

        // Rootless Podman socket lives under XDG_RUNTIME_DIR; fall back to the system socket.
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var path = !string.IsNullOrEmpty(xdg)
            ? $"{xdg}/podman/podman.sock"
            : "/run/podman/podman.sock";
        return new Uri($"unix://{path}");
    }
}
