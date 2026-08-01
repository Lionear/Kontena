using Kontena.Sdk.Tooling;

namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// Creates and removes local Kubernetes clusters — the third thing, next to the CEAL
/// (<c>IContainerEngine</c>) and the OAL (<see cref="IClusterEngine"/>).
/// <para>
/// Making a cluster is deliberately <b>not</b> part of <see cref="IClusterEngine"/>. That contract is
/// for talking <i>to</i> a cluster that exists: pods, nodes, apply, exec. There is no moment at which
/// one object can be both — before the create there is no cluster for an engine to be about.
/// </para>
/// <para>
/// The seam that makes the whole thing fit: a provisioner never registers anything. <c>kind</c> and
/// <c>minikube</c> write their own context into the kubeconfig, and the existing discovery
/// (<c>KubernetesClusterProvider.DiscoverAll</c>) picks it up. So this contract makes something, and
/// the code that was already looking sees it — the same separation that keeps CEAL and OAL apart.
/// </para>
/// </summary>
public interface IClusterProvisioner
{
    /// <summary>Stable id of the tool behind this provisioner: <c>kind</c>, <c>minikube</c>.</summary>
    string Provisioner { get; }

    /// <summary>How it is named in a sentence, e.g. "kind".</summary>
    string DisplayName { get; }

    /// <summary>
    /// What this provisioner can be asked for. The UI hides what is not offered rather than failing
    /// on it — kind cannot pause a cluster, minikube can.
    /// </summary>
    ProvisionerCapabilities Capabilities { get; }

    /// <summary>
    /// Whether the tool is present and new enough, via the shared tooling seam (KON-129). Never throws
    /// for a missing tool: being absent is an answer, and the page wants to say so and offer the
    /// install rather than fail.
    /// </summary>
    ValueTask<ToolReadiness> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// The clusters this provisioner made and still owns. Returns nothing when the tool is absent —
    /// a machine without kind has no kind clusters, which is not an error.
    /// </summary>
    ValueTask<IReadOnlyList<LocalCluster>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Which Kubernetes versions to offer for a new cluster (KON-144). Asked per provisioner, because
    /// the tools genuinely disagree: at the time of writing kind can boot v1.36.1 and minikube has
    /// never heard of it, so one shared list is wrong for one of them by construction.
    /// <para>
    /// Ask the tool where the tool can be asked. Never throws for an absent or unreadable tool — the
    /// form falls back to offering the tool's own default, which always works.
    /// </para>
    /// </summary>
    ValueTask<ClusterVersionOptions> VersionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Create a cluster, streaming the tool's own output line by line.
    /// <para>
    /// Streamed rather than awaited because this takes minutes — it pulls a node image and waits for
    /// the control plane — and buffered output is indistinguishable from a hang. A clean run ends by
    /// completing the sequence; a non-zero exit throws <see cref="ToolFailedException"/> at the end of
    /// enumeration, so a caller that only renders lines cannot show a failure as success.
    /// </para>
    /// </summary>
    /// <exception cref="ToolNotFoundException">The tool is not installed.</exception>
    /// <exception cref="ToolFailedException">The tool ran and exited non-zero.</exception>
    IAsyncEnumerable<ToolLine> CreateAsync(LocalClusterSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Delete a cluster and its kubeconfig context. Destructive — everything in it goes with it — so
    /// the caller confirms first (KON-126).
    /// </summary>
    /// <exception cref="ToolNotFoundException">The tool is not installed.</exception>
    /// <exception cref="ToolFailedException">The tool ran and exited non-zero.</exception>
    ValueTask DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Start a stopped cluster, streaming the tool's output — this brings a control plane back up and
    /// is not instant. Only where <see cref="ProvisionerCapabilities.StartStop"/> says so.
    /// </summary>
    /// <exception cref="NotSupportedException">This provisioner cannot stop a cluster, so it has none
    /// to start. Guard with the capability rather than catching this.</exception>
    IAsyncEnumerable<ToolLine> StartAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Stop a running cluster, keeping it. Not destructive: the workloads come back with it, which is
    /// the whole difference between this and <see cref="DeleteAsync"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">This provisioner cannot stop a cluster.</exception>
    ValueTask StopAsync(string name, CancellationToken ct = default);
}
