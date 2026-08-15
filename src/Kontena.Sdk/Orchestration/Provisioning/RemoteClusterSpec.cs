using System.Net;

namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// What to install on machines that already exist, alongside <see cref="LocalClusterSpec"/> rather than
/// derived from it (KON-232).
/// <para>
/// Two specs rather than one with optional halves, because the two jobs disagree on their first
/// question. A local spec asks <i>how many nodes</i> and the tool makes them; this one asks <i>which
/// machines</i>, and they are already there — with roles to assign, a network to agree on and an
/// endpoint clients will keep using after a controller is replaced. Folding those into
/// <see cref="LocalClusterSpec"/> would leave every field on it nullable and every reader guessing
/// which half it was handed.
/// </para>
/// </summary>
/// <param name="Name">Cluster name, same rules as a local one — it becomes the kubeconfig context.</param>
/// <param name="Hosts">The machines to install on. See <see cref="RemoteClusterHost"/>.</param>
public sealed record RemoteClusterSpec(string Name, IReadOnlyList<RemoteClusterHost> Hosts)
{
    /// <summary>
    /// Kubernetes version to install, e.g. <c>v1.31.0</c>. Null means the provisioner's own default,
    /// which is the version that release was tested with.
    /// </summary>
    public string? KubernetesVersion { get; init; }

    /// <summary>
    /// Which CNI to install, by the provisioner's own name for it, or null for its default. Only
    /// meaningful where <see cref="ProvisionerCapabilities.ChoosesCni"/> says so: kubeadm ships none at
    /// all and the cluster stays NotReady until one arrives, while k0s installs kube-router unless told
    /// otherwise.
    /// </summary>
    public string? Cni { get; init; }

    /// <summary>Pod network in CIDR form, or null for the provisioner's default. Has to match what the
    /// CNI is configured with — they are two settings for one network, and disagreeing is silent.</summary>
    public string? PodCidr { get; init; }

    /// <summary>Service network in CIDR form, or null for the provisioner's default.</summary>
    public string? ServiceCidr { get; init; }

    /// <summary>
    /// The address clients and joining nodes use for the API server — a load balancer or a floating IP,
    /// optionally with a port. Null points them at the first controller instead, which is fine for one
    /// controller and a trap for several: baked into every kubeconfig and every node's join, it turns
    /// that one machine into the cluster's single point of failure after all.
    /// </summary>
    public string? ControlPlaneEndpoint { get; init; }

    /// <summary>Hosts that will run the control plane.</summary>
    public int ControllerCount => Hosts.Count(h => h.Role == ClusterHostRole.Controller);

    /// <summary>
    /// What makes this spec unusable, or null when nothing does. One reason at a time, like
    /// <see cref="LocalClusterName.Problem"/> — the form shows the next one after the first is fixed.
    /// </summary>
    public string? Problem()
    {
        if (LocalClusterName.Problem(Name) is { } nameProblem)
            return nameProblem;

        if (Hosts.Count == 0)
            return "Add at least one machine — a remote cluster is a list of hosts, not a single node.";

        foreach (var host in Hosts)
        {
            if (Uri.CheckHostName(host.Address) == UriHostNameType.Unknown)
            {
                return string.IsNullOrWhiteSpace(host.Address)
                    ? "Give every machine an address."
                    : $"'{host.Address}' is not an IP address or a hostname.";
            }
        }

        var duplicate = Hosts
            .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            return $"'{duplicate.Key}' is listed twice — one machine can hold one role.";

        if (ControllerCount == 0)
            return "Mark at least one machine as a controller — without one there is no control plane.";

        if (CidrProblem(PodCidr, "pod network") is { } podProblem)
            return podProblem;

        return CidrProblem(ServiceCidr, "service network");
    }

    /// <summary>Whether the spec can be rolled out as it stands. Warnings do not count against it.</summary>
    public bool IsValid() => Problem() is null;

    /// <summary>
    /// What is legal but probably not meant, with the reason. Advisory on purpose: someone rehearsing a
    /// controller failure has a real use for a shape we would otherwise refuse, and a tool that blocks
    /// what it merely disagrees with is one people learn to work around.
    /// </summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        var controllers = ControllerCount;

        if (controllers > 0 && controllers % 2 == 0)
        {
            warnings.Add(
                $"{controllers} controllers is an even number, so etcd's quorum is " +
                $"{controllers / 2 + 1} of {controllers} and the cluster survives " +
                $"{controllers / 2 - 1} of them failing — the same as {controllers - 1} would, on one " +
                "machine fewer. Use an odd number: 1, or 3 for high availability.");
        }

        if (controllers > 1 && string.IsNullOrWhiteSpace(ControlPlaneEndpoint))
        {
            warnings.Add(
                "Several controllers, but no control-plane endpoint: every kubeconfig and every node " +
                "join would point at the first one, which puts the whole cluster behind the machine " +
                "the extra controllers exist to survive. Give a load balancer or floating IP.");
        }

        return warnings;
    }

    private static string? CidrProblem(string? cidr, string what) =>
        cidr is null || IPNetwork.TryParse(cidr, out _)
            ? null
            : $"'{cidr}' is not a {what} in CIDR form, e.g. 10.244.0.0/16.";
}
