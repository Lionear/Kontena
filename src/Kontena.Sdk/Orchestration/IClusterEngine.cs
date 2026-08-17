using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Sdk.Orchestration;

/// <summary>
/// The Orchestration Abstraction Layer (OAL) — sibling to the CEAL's <c>IContainerEngine</c>.
/// <para>
/// One stable, orchestrator-neutral contract that every cluster adapter (Kubernetes first,
/// and later others) implements. It is <b>not</b> an extension of the CEAL: a Pod is not a
/// Container, and declarative reconcile (apply YAML, controllers converge) is a different model
/// than imperative create/start/stop. Both share only the thin <see cref="IBackend"/> spine
/// (id, info, ping) and reusable streaming primitives (<see cref="LogEntry"/>,
/// <see cref="IExecSession"/>); everything else is its own shape.
/// </para>
/// <para>
/// Design principle (as with the CEAL): <b>model the union, expose the intersection, degrade
/// gracefully.</b> The generic declarative core (<see cref="ApplyAsync"/>,
/// <see cref="GetManifestAsync"/>, <see cref="DeleteAsync"/>, <see cref="WatchAsync"/>) works
/// for any <see cref="GroupVersionKind"/> — including CRDs — while typed listers back the grids.
/// <see cref="Capabilities"/> lets the UI adapt to what each cluster actually offers.
/// </para>
/// </summary>
public interface IClusterEngine : IBackend
{
    /// <summary>What this cluster supports; the UI queries this to adapt.</summary>
    ClusterCapabilities Capabilities { get; }

    // ── Contexts ─────────────────────────────────────────────────────────────

    /// <summary>Kube-contexts this engine exposes (a kubeconfig may hold several).</summary>
    IReadOnlyList<KubeContext> Contexts { get; }

    /// <summary>Switch the active context. Subsequent calls target the new cluster.</summary>
    ValueTask UseContextAsync(string name, CancellationToken ct = default);

    // ── Generic declarative core (the new primitives) ────────────────────────

    /// <summary>
    /// Apply a manifest bundle (<c>kubectl apply -f</c>). Streams one
    /// <see cref="ApplyProgress"/> per resource; with <see cref="ManifestBundle.DryRun"/> it
    /// validates and diffs server-side without persisting.
    /// </summary>
    /// <param name="status">
    /// Where to say what the apply is doing between results — reading the bundle, which resource of
    /// how many, waiting for a just-created CRD. A bundle can spend half a minute inside a single
    /// step, and a stream that only reports finished resources says nothing at all while it does
    /// (KON-381). Null when nobody is watching.
    /// </param>
    IAsyncEnumerable<ApplyProgress> ApplyAsync(
        ManifestBundle bundle, IProgress<string>? status = null, CancellationToken ct = default);

    /// <summary>Fetch a resource's live manifest (YAML).</summary>
    ValueTask<string> GetManifestAsync(ResourceRef resource, CancellationToken ct = default);

    /// <summary>Delete a resource. <paramref name="force"/> skips graceful termination.</summary>
    ValueTask DeleteAsync(ResourceRef resource, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Watch a resource kind (informer). Yields add/modify/delete events for the given
    /// <see cref="GroupVersionKind"/> in <paramref name="namespace"/> (null = all namespaces),
    /// until cancelled. Requires <see cref="ClusterCapabilities.Watch"/>.
    /// </summary>
    IAsyncEnumerable<ResourceEvent> WatchAsync(
        GroupVersionKind kind, string? ns = null, CancellationToken ct = default);

    // ── Generic resources (KON-75) ───────────────────────────────────────────

    /// <summary>
    /// Every resource type the cluster serves, custom ones included. Requires
    /// <see cref="ClusterCapabilities.Crds"/>.
    /// </summary>
    ValueTask<IReadOnlyList<ApiResource>> DiscoverResourcesAsync(CancellationToken ct = default);

    /// <summary>
    /// The raw OpenAPI v3 schema document for one API group+version, straight from the API server
    /// (<c>/openapi/v3/api/{version}</c> for the core group — pass <see cref="string.Empty"/> — or
    /// <c>/openapi/v3/apis/{group}/{version}</c> otherwise). Null when the cluster serves no such
    /// group/version.
    /// <para>
    /// Null is a distinct, honest answer, not an error: a schema-index built on this (Manifest Studio,
    /// KON-288) treats "this cluster does not serve it" as unverifiable rather than wrong, the same way
    /// an unresolved kind gets a <c>?</c> instead of a red squiggle.
    /// </para>
    /// </summary>
    ValueTask<string?> GetOpenApiSchemaAsync(string group, string version, CancellationToken ct = default);

    /// <summary>
    /// List one kind as the server renders it: the columns are its, not ours.
    /// <para>
    /// The counterpart to the typed listers above, and the reason a kind nobody modelled can still be
    /// browsed. <paramref name="ns"/> is ignored for cluster-scoped kinds.
    /// </para>
    /// </summary>
    ValueTask<ResourceTable> ListTableAsync(
        GroupVersionKind kind, string? ns = null, CancellationToken ct = default);

    // ── Typed listers (over the grids) ───────────────────────────────────────

    ValueTask<IReadOnlyList<KubeNamespace>> ListNamespacesAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<Node>> ListNodesAsync(CancellationToken ct = default);

    /// <summary>List workloads, optionally filtered by kind and/or namespace.</summary>
    ValueTask<IReadOnlyList<Workload>> ListWorkloadsAsync(
        WorkloadKind? kind = null, string? ns = null, CancellationToken ct = default);

    /// <summary>
    /// Which workload kinds exist here, in <see cref="WorkloadKind"/> order (KON-396).
    /// <para>
    /// Separate from <see cref="ListWorkloadsAsync"/> because it is a different question, and asking
    /// the expensive one to answer the cheap one is what made every cluster navigation cost the
    /// largest read in the app. The sidebar's per-kind submenu wants to know <i>whether</i> a kind is
    /// there, never which objects — and on a cluster that runs CronJobs, the finished Jobs it was
    /// downloading to find that out are the biggest list anywhere in the product.
    /// </para>
    /// <para>
    /// An implementation is expected to answer without fetching the objects: one object per kind is
    /// already one more than the question needs.
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<WorkloadKind>> ListWorkloadKindsAsync(
        string? ns = null, CancellationToken ct = default);

    ValueTask<IReadOnlyList<Pod>> ListPodsAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Service>> ListServicesAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Ingress>> ListIngressesAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PersistentVolumeClaim>> ListPvcsAsync(string? ns = null, CancellationToken ct = default);

    /// <summary>
    /// List PersistentVolumes — the other half of a claim (KON-254). Cluster-scoped, so no namespace.
    /// </summary>
    ValueTask<IReadOnlyList<PersistentVolume>> ListVolumesAsync(CancellationToken ct = default);

    /// <summary>
    /// List StorageClasses. Cluster-scoped. This is where a Pending claim's reason lives: a class
    /// with no provisioner, a class that does not exist, or a binding mode that is waiting on a pod.
    /// </summary>
    ValueTask<IReadOnlyList<StorageClass>> ListStorageClassesAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(string? ns = null, CancellationToken ct = default);

    /// <summary>List ConfigMaps — keys and sizes, not values (KON-249).</summary>
    ValueTask<IReadOnlyList<ConfigMapSummary>> ListConfigMapsAsync(string? ns = null, CancellationToken ct = default);

    /// <summary>
    /// List Secrets — keys and sizes, never values.
    /// <para>
    /// The list API hands over the values whether or not anyone wants them; an implementation is
    /// expected to keep the keys and drop the rest, so that nothing downstream of this call is able
    /// to render or log a secret it was never asked for. Values come from
    /// <see cref="GetConfigDataAsync"/>, one object at a time and only when asked.
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<SecretSummary>> ListSecretsAsync(string? ns = null, CancellationToken ct = default);

    /// <summary>
    /// Fetch the values of one ConfigMap or Secret, decoded.
    /// <para>
    /// Separate from the listers on purpose: a page that shows fifty secrets holds none of their
    /// values, and asking for one is a deliberate act with a single object's name attached to it.
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<ConfigEntry>> GetConfigDataAsync(ResourceRef resource, CancellationToken ct = default);

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>Scale a workload to <paramref name="replicas"/>.</summary>
    ValueTask ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct = default);

    /// <summary>Trigger a rolling restart of a workload (<c>kubectl rollout restart</c>).</summary>
    ValueTask RolloutRestartAsync(ResourceRef workload, CancellationToken ct = default);

    /// <summary>
    /// Mark a node unschedulable, or schedulable again (KON-251). Requires
    /// <see cref="ClusterCapabilities.NodeMaintenance"/>.
    /// </summary>
    ValueTask CordonNodeAsync(string node, bool cordoned, CancellationToken ct = default);

    /// <summary>
    /// Move the work off a node: cordon it, then evict what can be evicted, streaming one
    /// <see cref="DrainProgress"/> per decision.
    /// <para>
    /// <b>The eviction API, not delete.</b> That is what consults PodDisruptionBudgets, and a budget
    /// refusing is a true statement about the cluster rather than a failure of the drain.
    /// </para>
    /// <para>
    /// <b>A failed drain rolls nothing back.</b> A half-drained node stays cordoned, because that is
    /// the safe state and undoing it would put work back onto a node someone is about to touch. The
    /// stream says how far it got; putting the node back into service is a separate, deliberate act.
    /// </para>
    /// </summary>
    IAsyncEnumerable<DrainProgress> DrainNodeAsync(
        string node, DrainOptions options, CancellationToken ct = default);

    /// <summary>
    /// Open an interactive exec session into a pod container. Reuses the CEAL's
    /// <see cref="IExecSession"/> — the duplex PTY channel is identical.
    /// Requires <see cref="ClusterCapabilities.Exec"/>.
    /// </summary>
    ValueTask<IExecSession> StartExecSessionAsync(
        ResourceRef pod, string container, ExecRequest request, CancellationToken ct = default);

    /// <summary>
    /// Forward a local port to a pod/service port. Requires
    /// <see cref="ClusterCapabilities.PortForward"/>. <paramref name="localPort"/> null = auto-pick.
    /// </summary>
    ValueTask<IPortForward> PortForwardAsync(
        ResourceRef target, int remotePort, int? localPort = null, CancellationToken ct = default);

    // ── Streams (reuse LogEntry; PodMetrics mirrors ContainerStats) ───────────

    /// <summary>
    /// Stream a pod container's logs. <paramref name="follow"/> keeps the stream open.
    /// <para>
    /// <paramref name="previous"/> asks for the run that ended instead of the one that is running —
    /// the only place a crash-looping container's reason for exiting still exists, since the live
    /// stream belongs to the attempt that has not failed yet (KON-150). A container that has not
    /// restarted has no previous run, and the stream is then empty rather than an error.
    /// </para>
    /// </summary>
    IAsyncEnumerable<LogEntry> StreamLogsAsync(
        ResourceRef pod, string container, bool follow = true, bool previous = false,
        CancellationToken ct = default);

    /// <summary>Stream live pod metrics. Requires <see cref="ClusterCapabilities.Metrics"/>.</summary>
    IAsyncEnumerable<PodMetrics> StreamMetricsAsync(ResourceRef pod, CancellationToken ct = default);

    /// <summary>Stream cluster events for <paramref name="namespace"/> (null = all).</summary>
    IAsyncEnumerable<ResourceEvent> StreamEventsAsync(string? ns = null, CancellationToken ct = default);
}
