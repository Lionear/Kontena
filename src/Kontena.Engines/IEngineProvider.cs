namespace Kontena.Engines;

/// <summary>
/// Describes and creates a container-engine backend. This is the extensibility seam:
/// built-in backends (Docker, Podman) register as providers, and future store-installed
/// adapters will contribute providers too. The <see cref="EngineRegistry"/> discovers
/// providers and probes them for availability.
/// </summary>
public interface IEngineProvider
{
    /// <summary>Stable backend id, e.g. "docker", "podman".</summary>
    string Backend { get; }

    /// <summary>Human-facing name shown in the switcher, e.g. "Docker".</summary>
    string DisplayName { get; }

    /// <summary>Single-letter chip for the switcher, e.g. "D".</summary>
    string Chip { get; }

    /// <summary>Create a fresh engine instance for this backend.</summary>
    IContainerEngine CreateEngine();
}
