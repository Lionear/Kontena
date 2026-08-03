using Kontena.Adapters.Docker;
using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Podman;

/// <summary>
/// Built-in provider for Podman. Podman exposes a Docker-compatible API socket, so it
/// reuses <see cref="DockerEngine"/> pointed at the Podman socket with a Podman identity.
/// </summary>
public sealed class PodmanEngineProvider : IBackendProvider
{
    public string Backend => "podman";
    public string DisplayName => "Podman";
    public string Chip => "P";
    public BackendChipStyle? ChipStyle => new(PodmanBrand.Glyph, PodmanBrand.Accent);
    public BackendKind Kind => BackendKind.Engine;

    public IBackend CreateBackend() => new DockerEngine(PodmanEndpoint(), "podman", "Podman");

    /// <summary>
    /// The same three traces Docker's provider looks for, at Podman's own addresses (KON-255). The
    /// socket is the one <see cref="PodmanEndpoint"/> would connect to, so "installed" and "where we
    /// would look" cannot drift apart.
    /// <para>
    /// The CLI is what usually answers here: rootless Podman only opens its socket once
    /// <c>podman.socket</c> is enabled (see <see cref="PodmanSocketFix"/>), so on a fresh install the
    /// binary is present and the socket is not — and that machine has Podman.
    /// </para>
    /// </summary>
    public bool IsInstalled => EnginePresence.Any(
        environmentVariable: "CONTAINER_HOST",
        // LocalPath of the unix:// endpoint above; on Windows the pipe name is used instead.
        socketPath: PodmanEndpoint().LocalPath,
        windowsPipe: "podman-machine-default",
        executable: "podman");

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
