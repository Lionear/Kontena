using Kontena.Sdk;

namespace Kontena.Adapters.Podman;

/// <summary>Anchor for the Podman adapter (KON-28) — the CEAL against Podman's Docker-compatible API.</summary>
public static class PodmanAdapterModule
{
    /// <summary>Backend identifier used by the engine registry.</summary>
    public const string BackendId = "podman";

    /// <summary>How this adapter describes itself in Settings › Extensions (KON-283).</summary>
    public static EngineManifest Manifest { get; } = new()
    {
        Id = BackendId,
        Name = "Podman",
        Version = "1.0",
        Author = "Kontena",
        Description =
            "Rootless containers, pods and images through Podman's Docker-compatible socket.",
    };
}
