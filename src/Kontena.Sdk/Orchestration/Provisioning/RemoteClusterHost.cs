namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// One machine a remote cluster is to be installed on.
/// <para>
/// <see cref="User"/> and <see cref="KeyPath"/> are here, but no secret is: a username and a path to a
/// private key are not themselves the key (KON-233). The key never leaves the agent or the file it
/// lives in, which is the same line Kontena already draws for a remote engine over SSH — so this
/// record stays safe to log, diff and write into a config file.
/// </para>
/// </summary>
/// <param name="Address">Where to reach the machine — an IP address or a hostname, no scheme and no
/// port. Also what the node is called when <see cref="NodeName"/> is not given.</param>
/// <param name="Role">Controller or worker. See <see cref="ClusterHostRole"/>.</param>
public sealed record RemoteClusterHost(string Address, ClusterHostRole Role)
{
    /// <summary>
    /// What the node is called in the cluster, or null to leave it to the machine's own hostname. Worth
    /// having: an address is how we reach it, which is not always how it should be listed in
    /// <c>kubectl get nodes</c>.
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>The user to log in as, or null for the tool's own default.</summary>
    public string? User { get; init; }

    /// <summary>
    /// Path to the private key to log in with, or null to leave it to the SSH agent. The path, never
    /// the key — see the note on this type.
    /// </summary>
    public string? KeyPath { get; init; }
}
