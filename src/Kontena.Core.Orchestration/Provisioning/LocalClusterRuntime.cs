namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// Which container runtime hosts the cluster's nodes.
/// </summary>
public enum LocalClusterRuntime
{
    /// <summary>Whatever the tool picks by itself. The right answer on a machine with one runtime.</summary>
    Default,

    /// <summary>Docker.</summary>
    Docker,

    /// <summary>Podman.</summary>
    Podman,
}
