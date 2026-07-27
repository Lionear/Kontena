namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// A cluster one of the provisioners made and still owns.
/// </summary>
/// <param name="Name">The name it was created with — what the tool answers with, and what deletes it.</param>
/// <param name="Provisioner">Which provisioner owns it, matching <see cref="IClusterProvisioner.Provisioner"/>.</param>
/// <param name="Context">
/// The kubeconfig context it writes, e.g. <c>kind-dev</c> for a kind cluster called <c>dev</c>. This is
/// the join to the rest of Kontena: the same string appears in the backend id
/// (<c>kubernetes:kind-dev</c>) that discovery already produces, so a page can tell which switcher
/// entry belongs to which local cluster without asking the tool again.
/// </param>
public sealed record LocalCluster(string Name, string Provisioner, string Context)
{
    /// <summary>
    /// The cluster's nodes, as the provisioner names them. Empty when they were not asked for.
    /// <para>
    /// Node <i>names</i> rather than roles: kind calls them <c>dev-control-plane</c> and
    /// <c>dev-worker2</c>, but that is a naming habit, not a contract — reading a role out of a string
    /// would invent a fact. The count is what can be shown truthfully.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Nodes { get; init; } = [];

    /// <summary>
    /// Whether it is running, as far as the provisioner will say. <see cref="LocalClusterState.Unknown"/>
    /// is the honest answer for a tool that does not report it — kind has no notion of a stopped
    /// cluster, so claiming "running" for one of its clusters would be inventing a fact.
    /// </summary>
    public LocalClusterState State { get; init; } = LocalClusterState.Unknown;

    /// <summary>What the nodes run on, in the tool's own word ("docker", "kvm2"), or null.</summary>
    public string? Driver { get; init; }
}
