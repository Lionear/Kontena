using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kontena.Core;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

// Both sides name their watch enum WatchEventType and both namespaces are imported, so name each.
using K8sWatch = k8s.WatchEventType;
using WatchEvent = Kontena.Core.Orchestration.Models.WatchEventType;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// The real Kubernetes implementation of the OAL, talking to an apiserver through the official
/// client (KON-68).
/// <para>
/// This first cut covers <b>read and watch</b>: identity, the typed listers behind every grid,
/// informer streams, live manifests, pod logs, events, and usage via a pluggable
/// <see cref="IMetricsSource"/>. The mutating half of the contract — apply/dry-run, delete, scale,
/// rollout-restart — plus exec and port-forward are their own tickets, and
/// <see cref="Capabilities"/> reports them as absent so the UI hides those affordances rather than
/// offering something that would throw. That is exactly the degrade-gracefully contract the OAL was
/// designed around.
/// </para>
/// </summary>
public sealed class KubernetesClusterEngine : IClusterEngine, IMetricsAware, IDisposable
{
    private readonly k8s.Kubernetes _client;
    private readonly ClusterMetrics _metrics;
    private readonly KubernetesApply _apply;
    private readonly List<KubeContext> _contexts;

    private string _context;
    private ClusterCapabilities _capabilities;

    /// <param name="context">The kube-context to connect through.</param>
    public KubernetesClusterEngine(string context)
    {
        _context = context;
        _contexts = [.. Kubeconfig.LoadContexts()];
        _client = new k8s.Kubernetes(Kubeconfig.ConfigFor(context));
        _metrics = new ClusterMetrics(
            new MetricsServerSource(_client),
            new KubeletSummarySource(_client, NodeNamesAsync));
        _apply = new KubernetesApply(_client, new ApiResourceResolver(_client));

        // Metrics start off; PingAsync probes for a source and turns the gauges on if one answers.
        _capabilities = BaseCapabilities with { Metrics = false };
    }

    /// <summary>
    /// What this adapter genuinely supports today. Watch and CRD browsing are real; the mutating
    /// features are deliberately false until their own tickets land.
    /// </summary>
    private static ClusterCapabilities BaseCapabilities => new()
    {
        Watch = true,
        Crds = true,
        Apply = true,
        Metrics = false,
        Exec = false,
        PortForward = false,
        Helm = false,
    };

    public string Backend => KubernetesAdapterModule.BackendId;

    public ClusterCapabilities Capabilities => _capabilities;

    public IReadOnlyList<KubeContext> Contexts => _contexts;

    public IMetricsSource Metrics => _metrics;

    // ── Identity & health ────────────────────────────────────────────────────

    public async ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var version = await _client.Version.GetCodeAsync(ct).ConfigureAwait(false);
        var nodes = await _client.CoreV1.ListNodeAsync(cancellationToken: ct).ConfigureAwait(false);

        return new ClusterInfo
        {
            Backend = Backend,
            DisplayName = _context,
            Kind = "Kubernetes",
            Version = version.GitVersion ?? $"v{version.Major}.{version.Minor}",
            Endpoint = _client.BaseUri?.ToString() ?? string.Empty,
            ConnectionState = EngineConnectionState.Connected,
            Distribution = DistributionOf(nodes.Items, version.GitVersion),
            NodeCount = nodes.Items?.Count ?? 0,
            Context = _context,
        };
    }

    /// <summary>
    /// Name the distribution from the telltales each one leaves: kind and k3s stamp the kubelet
    /// version, the managed offerings label their nodes. Cosmetic — it drives the switcher chip.
    /// </summary>
    private static string DistributionOf(IList<V1Node>? nodes, string? gitVersion)
    {
        if (gitVersion is not null)
        {
            if (gitVersion.Contains("k3s", StringComparison.OrdinalIgnoreCase))
                return "k3s";
            if (gitVersion.Contains("gke", StringComparison.OrdinalIgnoreCase))
                return "GKE";
            if (gitVersion.Contains("eks", StringComparison.OrdinalIgnoreCase))
                return "EKS";
        }

        var first = nodes?.FirstOrDefault();
        var labels = first?.Metadata?.Labels ?? new Dictionary<string, string>();
        if (labels.ContainsKey("minikube.k8s.io/version"))
            return "minikube";
        if (first?.Metadata?.Name?.StartsWith("kind-", StringComparison.Ordinal) == true)
            return "kind";
        if (labels.ContainsKey("kubernetes.azure.com/cluster"))
            return "AKS";

        return "Kubernetes";
    }

    public async ValueTask PingAsync(CancellationToken ct = default)
    {
        // The apiserver's /version is the cheapest proof of a working connection and credentials.
        await _client.Version.GetCodeAsync(ct).ConfigureAwait(false);

        // Piggyback the metrics probe: one round-trip decides whether the UI shows usage gauges.
        var hasMetrics = await _metrics.ProbeAsync(ct).ConfigureAwait(false);
        _capabilities = BaseCapabilities with { Metrics = hasMetrics };
    }

    /// <summary>Node names for the kubelet source's per-node fan-out.</summary>
    private async Task<IReadOnlyList<string>> NodeNamesAsync(CancellationToken ct)
    {
        var nodes = await _client.CoreV1.ListNodeAsync(cancellationToken: ct).ConfigureAwait(false);
        return [.. (nodes.Items ?? []).Select(n => n.Metadata?.Name ?? string.Empty).Where(n => n.Length > 0)];
    }

    public ValueTask UseContextAsync(string name, CancellationToken ct = default)
    {
        // Switching context means a different apiserver and credentials, so the app creates a fresh
        // backend from the matching provider rather than mutating this one in place.
        if (_contexts.All(c => c.Name != name))
            throw new ArgumentException($"Unknown context '{name}'.", nameof(name));

        _context = name;
        for (var i = 0; i < _contexts.Count; i++)
            _contexts[i] = _contexts[i] with { IsCurrent = _contexts[i].Name == name };

        return ValueTask.CompletedTask;
    }

    // ── Typed listers ────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<KubeNamespace>> ListNamespacesAsync(CancellationToken ct = default)
    {
        var list = await _client.CoreV1.ListNamespaceAsync(cancellationToken: ct).ConfigureAwait(false);
        return [.. (list.Items ?? []).Select(K8sMap.ToNamespace)];
    }

    public async ValueTask<IReadOnlyList<Node>> ListNodesAsync(CancellationToken ct = default)
    {
        var list = await _client.CoreV1.ListNodeAsync(cancellationToken: ct).ConfigureAwait(false);
        var usage = await _metrics.GetNodeUsageAsync(ct).ConfigureAwait(false);
        var diskCapacity = await _metrics.GetNodeDiskCapacityAsync(ct).ConfigureAwait(false);

        // Pod counts come from the pod list, not the metrics source — they are always available.
        var pods = await _client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false);
        var perNode = (pods.Items ?? [])
            .Where(p => !string.IsNullOrEmpty(p.Spec?.NodeName))
            .GroupBy(p => p.Spec!.NodeName!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return
        [
            .. (list.Items ?? []).Select(n =>
            {
                var name = n.Metadata?.Name ?? string.Empty;
                var nodeUsage = usage.GetValueOrDefault(name);
                return K8sMap.ToNode(
                    n, nodeUsage, perNode.GetValueOrDefault(name), diskCapacity.GetValueOrDefault(name));
            }),
        ];
    }

    public async ValueTask<IReadOnlyList<Workload>> ListWorkloadsAsync(
        WorkloadKind? kind = null, string? ns = null, CancellationToken ct = default)
    {
        // One grid shows every controller kind, so fetch the requested kinds side by side.
        var wanted = kind is null ? Enum.GetValues<WorkloadKind>() : [kind.Value];
        var tasks = new List<Task<IReadOnlyList<Workload>>>();

        foreach (var k in wanted)
        {
            tasks.Add(k switch
            {
                WorkloadKind.Deployment => ListAsync<V1DeploymentList, V1Deployment>(ns, K8sMap.ToWorkload,
                    (n, c) => _client.AppsV1.ListNamespacedDeploymentAsync(n, cancellationToken: c),
                    c => _client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: c), ct),
                WorkloadKind.StatefulSet => ListAsync<V1StatefulSetList, V1StatefulSet>(ns, K8sMap.ToWorkload,
                    (n, c) => _client.AppsV1.ListNamespacedStatefulSetAsync(n, cancellationToken: c),
                    c => _client.AppsV1.ListStatefulSetForAllNamespacesAsync(cancellationToken: c), ct),
                WorkloadKind.DaemonSet => ListAsync<V1DaemonSetList, V1DaemonSet>(ns, K8sMap.ToWorkload,
                    (n, c) => _client.AppsV1.ListNamespacedDaemonSetAsync(n, cancellationToken: c),
                    c => _client.AppsV1.ListDaemonSetForAllNamespacesAsync(cancellationToken: c), ct),
                WorkloadKind.Job => ListAsync<V1JobList, V1Job>(ns, K8sMap.ToWorkload,
                    (n, c) => _client.BatchV1.ListNamespacedJobAsync(n, cancellationToken: c),
                    c => _client.BatchV1.ListJobForAllNamespacesAsync(cancellationToken: c), ct),
                WorkloadKind.CronJob => ListAsync<V1CronJobList, V1CronJob>(ns, K8sMap.ToWorkload,
                    (n, c) => _client.BatchV1.ListNamespacedCronJobAsync(n, cancellationToken: c),
                    c => _client.BatchV1.ListCronJobForAllNamespacesAsync(cancellationToken: c), ct),

                // ReplicaSets are an implementation detail of Deployments; the grid rolls them up.
                _ => Task.FromResult<IReadOnlyList<Workload>>([]),
            });
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.SelectMany(r => r).OrderBy(w => w.Namespace, StringComparer.Ordinal).ThenBy(w => w.Name, StringComparer.Ordinal)];
    }

    /// <summary>Namespaced-or-all listing, mapped — the shape every lister here repeats.</summary>
    private static async Task<IReadOnlyList<Workload>> ListAsync<TList, TItem>(
        string? ns,
        Func<TItem, Workload> map,
        Func<string, CancellationToken, Task<TList>> byNamespace,
        Func<CancellationToken, Task<TList>> allNamespaces,
        CancellationToken ct)
        where TList : IItems<TItem>
    {
        var list = ns is null
            ? await allNamespaces(ct).ConfigureAwait(false)
            : await byNamespace(ns, ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(map)];
    }

    public async ValueTask<IReadOnlyList<Pod>> ListPodsAsync(string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToPod)];
    }

    public async ValueTask<IReadOnlyList<Service>> ListServicesAsync(string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListServiceForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToService)];
    }

    public async ValueTask<IReadOnlyList<Ingress>> ListIngressesAsync(string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.NetworkingV1.ListIngressForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToIngress)];
    }

    public async ValueTask<IReadOnlyList<PersistentVolumeClaim>> ListPvcsAsync(
        string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToPvc)];
    }

    public async ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(
        string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListEventForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToEvent).OrderByDescending(e => e.LastSeen)];
    }

    // ── Watch (informer) ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<ResourceEvent> WatchAsync(
        GroupVersionKind kind, string? ns = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = WatchStream(kind, ns, ct);
        if (stream is null)
            yield break;

        await foreach (var (type, obj) in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            if (obj is not IMetadata<V1ObjectMeta> meta)
                continue;

            yield return new ResourceEvent
            {
                Type = type switch
                {
                    K8sWatch.Added => WatchEvent.Added,
                    K8sWatch.Deleted => WatchEvent.Deleted,
                    _ => WatchEvent.Modified,
                },
                Resource = new ResourceRef(kind, meta.Metadata?.NamespaceProperty, meta.Metadata?.Name ?? "?"),
                Manifest = KubernetesYaml.Serialize(obj),
            };
        }
    }

    /// <summary>
    /// The watch stream for a kind, or null when this adapter has no typed watcher for it. Bookmarks
    /// are off: Kontena rebuilds from the typed listers on reconnect rather than tracking revisions.
    /// </summary>
    private IAsyncEnumerable<(K8sWatch, object)>? WatchStream(
        GroupVersionKind kind, string? ns, CancellationToken ct) => kind.Kind switch
    {
        "Pod" => Box(ns is null
            ? _client.CoreV1.WatchListPodForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedPodAsync(ns, cancellationToken: ct)),
        "Service" => Box(ns is null
            ? _client.CoreV1.WatchListServiceForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedServiceAsync(ns, cancellationToken: ct)),
        "Node" => Box(_client.CoreV1.WatchListNodeAsync(cancellationToken: ct)),
        "Namespace" => Box(_client.CoreV1.WatchListNamespaceAsync(cancellationToken: ct)),
        "Deployment" => Box(ns is null
            ? _client.AppsV1.WatchListDeploymentForAllNamespacesAsync(cancellationToken: ct)
            : _client.AppsV1.WatchListNamespacedDeploymentAsync(ns, cancellationToken: ct)),
        "StatefulSet" => Box(ns is null
            ? _client.AppsV1.WatchListStatefulSetForAllNamespacesAsync(cancellationToken: ct)
            : _client.AppsV1.WatchListNamespacedStatefulSetAsync(ns, cancellationToken: ct)),
        "DaemonSet" => Box(ns is null
            ? _client.AppsV1.WatchListDaemonSetForAllNamespacesAsync(cancellationToken: ct)
            : _client.AppsV1.WatchListNamespacedDaemonSetAsync(ns, cancellationToken: ct)),
        _ => null,
    };

    /// <summary>Erase the element type so one loop can drive every watcher.</summary>
    private static async IAsyncEnumerable<(K8sWatch, object)> Box<T>(IAsyncEnumerable<(K8sWatch, T)> source)
        where T : notnull
    {
        await foreach (var (type, item) in source.ConfigureAwait(false))
            yield return (type, item);
    }

    // ── Manifests ────────────────────────────────────────────────────────────

    public async ValueTask<string> GetManifestAsync(ResourceRef resource, CancellationToken ct = default)
    {
        var ns = resource.Namespace;
        var name = resource.Name;

        object? obj = resource.Kind.Kind switch
        {
            "Pod" => await _client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "Service" => await _client.CoreV1.ReadNamespacedServiceAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "Namespace" => await _client.CoreV1.ReadNamespaceAsync(name, cancellationToken: ct).ConfigureAwait(false),
            "Node" => await _client.CoreV1.ReadNodeAsync(name, cancellationToken: ct).ConfigureAwait(false),
            "PersistentVolumeClaim" => await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "Ingress" => await _client.NetworkingV1.ReadNamespacedIngressAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "Deployment" => await _client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "StatefulSet" => await _client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "DaemonSet" => await _client.AppsV1.ReadNamespacedDaemonSetAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "Job" => await _client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            "CronJob" => await _client.BatchV1.ReadNamespacedCronJobAsync(name, ns, cancellationToken: ct).ConfigureAwait(false),
            _ => null,
        };

        return obj is null
            ? $"# {resource.Kind.Kind} is not a kind this adapter can read yet."
            : KubernetesYaml.Serialize(obj);
    }

    // ── Streams ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        ResourceRef pod, string container, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        HttpOperationResponse<Stream>? response = null;
        try
        {
            response = await _client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
                pod.Name, pod.Namespace, container: container, follow: follow,
                tailLines: 500, timestamps: true, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A pod that is still pulling its image has no log yet — an empty stream, not a crash.
            yield break;
        }

        using (response)
        {
            using var reader = new StreamReader(response.Body);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                yield return ParseLogLine(line);
            }
        }
    }

    /// <summary>
    /// Split the RFC3339 timestamp kubelet prefixes onto each line. Everything a pod writes is one
    /// stream, so the source is always <see cref="LogSource.Stdout"/>.
    /// </summary>
    private static LogEntry ParseLogLine(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && DateTimeOffset.TryParse(line[..space], out var stamp))
            return new LogEntry(stamp, LogSource.Stdout, line[(space + 1)..]);

        return new LogEntry(DateTimeOffset.UtcNow, LogSource.Stdout, line);
    }

    public async IAsyncEnumerable<Core.Orchestration.Models.PodMetrics> StreamMetricsAsync(
        ResourceRef pod, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_metrics.IsAvailable)
            yield break;

        // metrics.k8s.io is a sampled snapshot API, not a stream — poll it at the scrape interval.
        while (!ct.IsCancellationRequested)
        {
            var samples = await _metrics.GetPodUsageAsync(pod.Namespace, ct).ConfigureAwait(false);
            var mine = samples.FirstOrDefault(s => s.Pod == pod.Name);
            if (mine is not null)
                yield return mine;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public async IAsyncEnumerable<ResourceEvent> StreamEventsAsync(
        string? ns = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = ns is null
            ? _client.CoreV1.WatchListEventForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedEventAsync(ns, cancellationToken: ct);

        await foreach (var (type, e) in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return new ResourceEvent
            {
                Type = type == K8sWatch.Deleted ? WatchEvent.Deleted : WatchEvent.Added,
                Resource = K8sMap.ToEvent(e).InvolvedObject,
                Manifest = e.Message,
            };
        }
    }

    // ── Mutations (KON-86) ───────────────────────────────────────────────────

    /// <summary>
    /// Apply a bundle through server-side apply, one result per document. With
    /// <see cref="ManifestBundle.DryRun"/> the API server runs the full admission chain and returns
    /// what the object would become, without persisting — so the plan comes from the cluster rather
    /// than from a local guess.
    /// </summary>
    public async IAsyncEnumerable<ApplyProgress> ApplyAsync(
        ManifestBundle bundle, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var documents = ManifestDocuments.Split(bundle.Yaml).ToList();
        var pendingNamespaces = ManifestDocuments.NamespacesCreatedBy(documents);

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();

            if (document.Error is { } error)
            {
                yield return new ApplyProgress
                {
                    Resource = new ResourceRef(GroupVersionKind.Pod, null, "?"),
                    Action = ApplyAction.Failed,
                    Error = error,
                };
                continue;
            }

            yield return await _apply
                .ApplyOneAsync(document.Content!, bundle.DryRun, DefaultNamespace, pendingNamespaces, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The context's namespace, used for documents that name none.</summary>
    private string? DefaultNamespace =>
        _contexts.FirstOrDefault(c => c.Name == _context)?.Namespace;

    public async ValueTask DeleteAsync(ResourceRef resource, bool force = false, CancellationToken ct = default) =>
        await _apply.DeleteAsync(resource, force, ct).ConfigureAwait(false);

    public async ValueTask ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct = default) =>
        await _apply.ScaleAsync(workload, replicas, ct).ConfigureAwait(false);

    public async ValueTask RolloutRestartAsync(ResourceRef workload, CancellationToken ct = default) =>
        await _apply.RolloutRestartAsync(workload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

    // ── Not in this cut ──────────────────────────────────────────────────────

    private static NotSupportedException NotYet(string feature, string ticket) =>
        new($"{feature} is not implemented by the Kubernetes adapter yet ({ticket}). " +
            "Capabilities reports it as unavailable, so the UI should not offer it.");

    public ValueTask<IExecSession> StartExecSessionAsync(
        ResourceRef pod, string container, ExecRequest request, CancellationToken ct = default) =>
        throw NotYet("Exec", "KON-68 follow-up");

    public ValueTask<IPortForward> PortForwardAsync(
        ResourceRef target, int remotePort, int? localPort = null, CancellationToken ct = default) =>
        throw NotYet("Port-forward", "KON-68 follow-up");

    public void Dispose() => _client.Dispose();
}
