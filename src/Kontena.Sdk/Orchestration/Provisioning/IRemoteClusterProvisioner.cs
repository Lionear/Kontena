using Kontena.Sdk.Tooling;

namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// Installs Kubernetes on machines that already exist — kubeadm, k0s, Talos (KON-236).
/// <para>
/// A sibling of <see cref="IClusterProvisioner"/> rather than an extension of it, which is the same
/// call KON-232 made for the specs and for the same reason. The two jobs disagree on their first
/// question: a local provisioner is asked how many nodes to make, this one is told which machines to
/// use. Folding them together would put a <c>CreateAsync(RemoteClusterSpec)</c> on kind and minikube
/// that could only throw, and every caller would have to know which half of the interface it was
/// holding.
/// </para>
/// <para>
/// Nothing here lists, deletes, starts or stops. A local cluster is Kontena's to unmake because
/// Kontena made the containers; a remote one is somebody's fleet of machines, and tearing it down is a
/// different conversation with a different confirmation. <see cref="IClusterProvisioner"/> keeps those
/// verbs because it can honour them safely.
/// </para>
/// <para>
/// Like its sibling it registers nothing: <c>k0sctl</c> writes a kubeconfig, and the discovery that
/// already exists turns that into a backend.
/// </para>
/// </summary>
public interface IRemoteClusterProvisioner
{
    /// <summary>Stable id of the tool behind this provisioner: <c>k0s</c>, later <c>kubeadm</c>.</summary>
    string Provisioner { get; }

    /// <summary>How it is named in a sentence, e.g. "k0s".</summary>
    string DisplayName { get; }

    /// <summary>
    /// What this provisioner can be asked for. <see cref="ProvisionerCapabilities.NeedsHosts"/> is
    /// true for everything here by definition — it is what selects this contract over the other.
    /// </summary>
    ProvisionerCapabilities Capabilities { get; }

    /// <summary>
    /// Whether the tool is present and new enough, through the shared tooling seam (KON-109). Never
    /// throws for a missing tool: being absent is an answer the page wants to show, with the install.
    /// </summary>
    ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Which Kubernetes versions to offer, or <see cref="ClusterVersionOptions.None"/> when this
    /// provisioner has no way to know without guessing or going online (KON-144's rule, applied
    /// honestly rather than by producing a list).
    /// </summary>
    ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default);

    /// <summary>
    /// The configuration this spec produces, as text, so it can be read before it runs. Not a
    /// convenience: this file is the whole cluster, and someone signing off on a rollout onto their own
    /// machines should be able to see exactly what will be applied.
    /// </summary>
    string Preview(RemoteClusterSpec spec, IClusterCredentials credentials);

    /// <summary>
    /// Install the cluster, streaming the tool's own output line by line.
    /// <para>
    /// Streamed for the same reason as the local one, only more so: this reaches out to several
    /// machines over SSH and takes minutes, and buffered output is indistinguishable from a hang. A
    /// non-zero exit throws <see cref="ToolFailedException"/> at the end of enumeration, so a caller
    /// that only renders lines cannot show a failure as success.
    /// </para>
    /// </summary>
    /// <exception cref="ToolNotFoundException">The tool is not installed.</exception>
    /// <exception cref="ToolFailedException">The tool ran and exited non-zero.</exception>
    IAsyncEnumerable<ToolLine> CreateAsync(
        RemoteClusterSpec spec, IClusterCredentials credentials, CancellationToken ct = default);
}
