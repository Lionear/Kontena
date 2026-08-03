using Kontena.Sdk;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Docker;

/// <summary>Built-in provider for the Docker engine.</summary>
public sealed class DockerEngineProvider : IBackendProvider
{
    public string Backend => "docker";
    public string DisplayName => "Docker";
    public string Chip => "D";
    public BackendChipStyle? ChipStyle => new(DockerBrand.Glyph, DockerBrand.Accent);
    public BackendKind Kind => BackendKind.Engine;
    public IBackend CreateBackend() => new DockerEngine();

    /// <summary>
    /// Any of the three traces a Docker installation leaves, in the order they cost: the environment
    /// variable that overrides where the engine is, the socket the engine opens, the CLI on PATH.
    /// <para>
    /// The CLI matters as much as the socket, and it is what makes this "installed" rather than
    /// "running": a stopped Docker has no socket, and hiding it then would take away the one row that
    /// says so (KON-255). <c>DOCKER_HOST</c> comes first because a user who set it has told us where
    /// their engine is, and looking for a local socket after that would be answering a question they
    /// already answered.
    /// </para>
    /// </summary>
    public bool IsInstalled => EnginePresence.Any(
        environmentVariable: "DOCKER_HOST",
        socketPath: "/var/run/docker.sock",
        windowsPipe: "docker_engine",
        executable: "docker");
}
