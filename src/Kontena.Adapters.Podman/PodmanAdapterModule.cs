namespace Kontena.Adapters.Podman;

/// <summary>
/// Placeholder anchor for the Podman adapter (KON-28). Will implement the
/// CEAL contract against Podman's Docker-compatible API socket (rootless).
/// </summary>
public static class PodmanAdapterModule
{
    /// <summary>Backend identifier used by the engine registry.</summary>
    public const string BackendId = "podman";
}
