namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// Whether a local cluster is up, as far as its provisioner reports.
/// </summary>
public enum LocalClusterState
{
    /// <summary>
    /// The provisioner does not say. Its own state, not a synonym for stopped: kind has no stopped
    /// state to report, and a UI that reads "unknown" as "off" would offer a Start that does nothing.
    /// </summary>
    Unknown,

    /// <summary>Up and answering.</summary>
    Running,

    /// <summary>Made, but not running. Startable without creating it again.</summary>
    Stopped,
}
