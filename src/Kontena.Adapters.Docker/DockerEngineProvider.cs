using Kontena.Core;
using Kontena.Engines;

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
}
