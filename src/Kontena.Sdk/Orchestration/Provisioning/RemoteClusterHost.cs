namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// One machine a remote cluster is to be installed on.
/// <para>
/// How to log in is deliberately not here — see KON-233. Credentials are per host too, but they are
/// secrets with a lifetime of their own: they are entered once, reused across clusters, and must not
/// end up in something a spec can be logged or serialised into.
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
}
