using Kontena.Sdk;

namespace Kontena.Adapters.Docker;

/// <summary>
/// Anchor for the Docker adapter (KON-27). Implements the CEAL contract against the Docker Engine API
/// over the Unix socket / Windows named pipe.
/// </summary>
public static class DockerAdapterModule
{
    /// <summary>Backend identifier used by the engine registry.</summary>
    public const string BackendId = "docker";

    /// <summary>
    /// How this adapter describes itself in Settings › Extensions (KON-283). The same shape an external
    /// plugin declares, so the page has one kind of thing to render rather than a built-in case beside
    /// a plugin case.
    /// </summary>
    public static EngineManifest Manifest { get; } = new()
    {
        Id = BackendId,
        Name = "Docker",
        Version = "1.0",
        Author = "Kontena",
        Description =
            "Containers, images, volumes, networks and Compose projects on a local or remote Docker "
            + "Engine.",
    };
}
