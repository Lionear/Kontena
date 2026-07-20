using Kontena.Engines;

namespace Kontena.Adapters.Docker;

/// <summary>Built-in provider for the Docker engine.</summary>
public sealed class DockerEngineProvider : IEngineProvider
{
    public string Backend => "docker";
    public string DisplayName => "Docker";
    public string Chip => "D";
    public IContainerEngine CreateEngine() => new DockerEngine();
}
