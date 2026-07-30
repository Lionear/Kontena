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
    IAsyncEnumerable<ApplyProgress> ApplyAsync(ManifestBundle bundle, CancellationToken ct = default);

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

    ValueTask<IReadOnlyList<Pod>> ListPodsAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Service>> ListServicesAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Ingress>> ListIngressesAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PersistentVolumeClaim>> ListPvcsAsync(string? ns = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(string? ns = null, CancellationToken ct = default);

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>Scale a workload to <paramref name="replicas"/>.</summary>
    ValueTask ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct = default);

    /// <summary>Trigger a rolling restart of a workload (<c>kubectl rollout restart</c>).</summary>
    ValueTask RolloutRestartAsync(ResourceRef workload, CancellationToken ct = default);

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

    /// <summary>Stream a pod container's logs. <paramref name="follow"/> keeps the stream open.</summary>
    IAsyncEnumerable<LogEntry> StreamLogsAsync(
        ResourceRef pod, string container, bool follow = true, CancellationToken ct = default);

    /// <summary>Stream live pod metrics. Requires <see cref="ClusterCapabilities.Metrics"/>.</summary>
    IAsyncEnumerable<PodMetrics> StreamMetricsAsync(ResourceRef pod, CancellationToken ct = default);

    /// <summary>Stream cluster events for <paramref name="namespace"/> (null = all).</summary>
    IAsyncEnumerable<ResourceEvent> StreamEventsAsync(string? ns = null, CancellationToken ct = default);
}
