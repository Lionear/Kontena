using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

// Both sides name their watch enum WatchEventType and both namespaces are imported, so name each.
using K8sWatch = k8s.WatchEventType;
using WatchEvent = Kontena.Sdk.Orchestration.Models.WatchEventType;

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
public sealed class KubernetesClusterEngine : IClusterEngine, IMetricsAware, IMetricsHistoryAware, IDisposable
{
    private readonly k8s.Kubernetes _client;
    private readonly ClusterMetrics _metrics;
    private readonly KubernetesApply _apply;
    private readonly ApiResourceResolver _resources;
    private readonly List<KubeContext> _contexts;

    private string _context;
    private ClusterCapabilities _capabilities;

    private readonly string? _kubeconfigPath;
    private readonly PrometheusSource _history;

    /// <param name="context">The kube-context to connect through.</param>
    /// <param name="kubeconfigPath">The kubeconfig it came from, or null for the default one (KON-118).</param>
    public KubernetesClusterEngine(string context, string? kubeconfigPath = null)
    {
        _context = context;
        _kubeconfigPath = string.IsNullOrWhiteSpace(kubeconfigPath) ? null : kubeconfigPath;
        _contexts = [.. Kubeconfig.LoadContexts(_kubeconfigPath)];
        var client = new k8s.Kubernetes(Kubeconfig.ConfigFor(context, _kubeconfigPath));
        _client = client;
        _metrics = new ClusterMetrics(
            new MetricsServerSource(_client),
            new KubeletSummarySource(_client, NodeNamesAsync));

        // The raw HttpClient rather than a generated operation: the service proxy has to carry a
        // query string through to Prometheus, and the generated method has nowhere to put one.
        _history = new PrometheusSource(client.HttpClient, client.BaseUri, client);
        _resources = new ApiResourceResolver(_client);
        _apply = new KubernetesApply(_client, _resources);

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
        Exec = true,
        PortForward = true,
        NodeMaintenance = true,
        Metrics = false,
        Helm = false,
    };

    public string Backend => KubernetesAdapterModule.BackendId;

    public ClusterCapabilities Capabilities => _capabilities;

    public IReadOnlyList<KubeContext> Contexts => _contexts;

    public IMetricsSource Metrics => _metrics;

    /// <summary>Where the past comes from, when the cluster keeps one (KON-345).</summary>
    public IMetricsHistory History => _history;

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
        try
        {
            // The apiserver's /version is the cheapest proof of a working connection and credentials.
            await _client.Version.GetCodeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ping is what the shell uses to decide whether a cluster is usable, so it is the one
            // place the reason has to survive in a form the app can act on.
            throw K8sErrors.Map(ex, _context);
        }

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

    // ── Generic resources (KON-75) ───────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ApiResource>> DiscoverResourcesAsync(CancellationToken ct = default) =>
        new(_resources.DiscoverAllAsync(ct));

    /// <inheritdoc/>
    public async ValueTask<string?> GetOpenApiSchemaAsync(
        string group, string version, CancellationToken ct = default)
    {
        using var response = await _client.HttpClient
            .GetAsync(OpenApiRequestUri(_client.BaseUri, group, version), ct)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Same core/named-group split, and the same trailing-slash care, as <see cref="ResourceTables.RequestUri"/>.</summary>
    internal static Uri OpenApiRequestUri(Uri baseUri, string group, string version)
    {
        var path = string.IsNullOrEmpty(group) ? $"openapi/v3/api/{version}" : $"openapi/v3/apis/{group}/{version}";

        // A base address without its trailing slash would swallow its last segment when combined.
        var rootUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");

        return new Uri(rootUri, path);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceTable> ListTableAsync(
        GroupVersionKind kind, string? ns = null, CancellationToken ct = default)
    {
        // Discovery decides the path, because the plural is the server's to name and a cluster-scoped
        // kind must not be asked for inside a namespace.
        if (await _resources.ResolveAsync(kind, ct).ConfigureAwait(false) is not { } resource)
            return ResourceTable.Empty;

        return await ResourceTables
            .ListAsync(_client.HttpClient, _client.BaseUri, resource, kind, ns, ct)
            .ConfigureAwait(false);
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

    public async ValueTask<IReadOnlyList<PersistentVolume>> ListVolumesAsync(CancellationToken ct = default)
    {
        var list = await _client.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct).ConfigureAwait(false);
        return [.. (list.Items ?? []).Select(K8sMap.ToVolume)];
    }

    public async ValueTask<IReadOnlyList<StorageClass>> ListStorageClassesAsync(CancellationToken ct = default)
    {
        var list = await _client.StorageV1.ListStorageClassAsync(cancellationToken: ct).ConfigureAwait(false);
        return [.. (list.Items ?? []).Select(K8sMap.ToStorageClass)];
    }

    public async ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(
        string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListEventForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToEvent).OrderByDescending(e => e.LastSeen)];
    }

    // ── Node maintenance (KON-251) ───────────────────────────────────────────

    public async ValueTask CordonNodeAsync(string node, bool cordoned, CancellationToken ct = default)
    {
        try
        {
            await NodeMaintenance.CordonAsync(_client, node, cordoned, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw K8sErrors.Map(ex, _context);
        }
    }

    public IAsyncEnumerable<DrainProgress> DrainNodeAsync(
        string node, DrainOptions options, CancellationToken ct = default) =>
        NodeMaintenance.DrainAsync(_client, node, options, ct);

    public async ValueTask<IReadOnlyList<ConfigMapSummary>> ListConfigMapsAsync(
        string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListConfigMapForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToConfigMap)];
    }

    /// <summary>
    /// List Secrets, keys only.
    /// <para>
    /// The values arrive over the wire regardless — the list API has no way to ask for a Secret
    /// without its data — and <see cref="K8sMap.ToSecret"/> is where they stop. Nothing this method
    /// returns can render, log or serialise a secret value, and the deserialised response is not
    /// held beyond the projection.
    /// </para>
    /// </summary>
    public async ValueTask<IReadOnlyList<SecretSummary>> ListSecretsAsync(
        string? ns = null, CancellationToken ct = default)
    {
        var list = ns is null
            ? await _client.CoreV1.ListSecretForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct).ConfigureAwait(false);

        return [.. (list.Items ?? []).Select(K8sMap.ToSecret)];
    }

    public async ValueTask<IReadOnlyList<ConfigEntry>> GetConfigDataAsync(
        ResourceRef resource, CancellationToken ct = default)
    {
        var ns = resource.Namespace ?? "default";

        return resource.Kind.Kind switch
        {
            "Secret" => K8sMap.ToEntries(
                await _client.CoreV1.ReadNamespacedSecretAsync(resource.Name, ns, cancellationToken: ct)
                    .ConfigureAwait(false)),

            "ConfigMap" => K8sMap.ToEntries(
                await _client.CoreV1.ReadNamespacedConfigMapAsync(resource.Name, ns, cancellationToken: ct)
                    .ConfigureAwait(false)),

            // Named rather than swallowed: a caller asking a third kind for its data has a bug, and
            // an empty list would look like an object with no keys.
            var kind => throw new NotSupportedException(
                $"{kind} has no configuration data; only ConfigMap and Secret do."),
        };
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
    /// The kinds <see cref="WatchStream"/> has a typed watcher for, as data.
    /// <para>
    /// A page that follows a kind this adapter cannot watch gets an empty stream, which the page
    /// reads as "the cluster closed the stream" — a confident, wrong explanation of a mistake made
    /// here. Stated separately so that claim can be checked without a cluster to check it against.
    /// Keep in step with the switch below; they are five lines apart for that reason.
    /// </para>
    /// </summary>
    public static bool CanWatch(GroupVersionKind kind) => WatchableKinds.Contains(kind.Kind);

    private static readonly HashSet<string> WatchableKinds = new(StringComparer.Ordinal)
    {
        "Pod", "Service", "Node", "Namespace",
        "Deployment", "StatefulSet", "DaemonSet",
        "Ingress", "PersistentVolumeClaim", "PersistentVolume", "StorageClass",
        "ConfigMap", "Secret", "Event",
        "Job", "CronJob",
    };

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
        "Ingress" => Box(ns is null
            ? _client.NetworkingV1.WatchListIngressForAllNamespacesAsync(cancellationToken: ct)
            : _client.NetworkingV1.WatchListNamespacedIngressAsync(ns, cancellationToken: ct)),
        "PersistentVolumeClaim" => Box(ns is null
            ? _client.CoreV1.WatchListPersistentVolumeClaimForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct)),
        // The batch kinds, missing since KON-250 (KON-344). The per-kind Jobs page always claimed to
        // follow batch/v1 Job, got an empty stream back, and blamed the cluster for closing it.
        "Job" => Box(ns is null
            ? _client.BatchV1.WatchListJobForAllNamespacesAsync(cancellationToken: ct)
            : _client.BatchV1.WatchListNamespacedJobAsync(ns, cancellationToken: ct)),
        "CronJob" => Box(ns is null
            ? _client.BatchV1.WatchListCronJobForAllNamespacesAsync(cancellationToken: ct)
            : _client.BatchV1.WatchListNamespacedCronJobAsync(ns, cancellationToken: ct)),
        "ConfigMap" => Box(ns is null
            ? _client.CoreV1.WatchListConfigMapForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedConfigMapAsync(ns, cancellationToken: ct)),
        // Only the metadata travels either way — the watch carries what the listing carries, and the
        // page holds key names and sizes, never a value (KON-249).
        "Secret" => Box(ns is null
            ? _client.CoreV1.WatchListSecretForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedSecretAsync(ns, cancellationToken: ct)),
        // Core v1, matching ListEventsAsync above rather than events.k8s.io.
        "Event" => Box(ns is null
            ? _client.CoreV1.WatchListEventForAllNamespacesAsync(cancellationToken: ct)
            : _client.CoreV1.WatchListNamespacedEventAsync(ns, cancellationToken: ct)),
        // Cluster-scoped, so no namespaced variant to choose between.
        "PersistentVolume" => Box(_client.CoreV1.WatchListPersistentVolumeAsync(cancellationToken: ct)),
        "StorageClass" => Box(_client.StorageV1.WatchListStorageClassAsync(cancellationToken: ct)),
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
        ResourceRef pod, string container, bool follow = true, bool previous = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        HttpOperationResponse<Stream>? response = null;
        try
        {
            response = await _client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
                pod.Name, pod.Namespace, container: container,
                // The previous run is finished, so there is nothing left to follow: asking the API to
                // follow it as well returns an error rather than a closed stream.
                follow: follow && !previous, previous: previous,
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
    /// <para>
    /// The splitting itself is <see cref="LogLine.Parse"/>, shared with the Docker adapter since
    /// KON-203 — this used to be the only place that did it, and the other one had drifted into
    /// stamping the read time onto every line.
    /// </para>
    /// </summary>
    private static LogEntry ParseLogLine(string line) =>
        LogLine.Parse(line, LogSource.Stdout, DateTimeOffset.UtcNow);

    public async IAsyncEnumerable<Kontena.Sdk.Orchestration.Models.PodMetrics> StreamMetricsAsync(
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
        var fallback = bundle.Namespace is { Length: > 0 } chosen ? chosen : DefaultNamespace;

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
                .ApplyOneAsync(document.Content!, bundle.DryRun, fallback, pendingNamespaces, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The context's namespace, for documents that name none. A kubeconfig context often sets
    /// none at all, and then the rule is kubectl's: "default" — not a refusal to apply.
    /// </summary>
    private string DefaultNamespace =>
        _contexts.FirstOrDefault(c => c.Name == _context)?.Namespace is { Length: > 0 } ns
            ? ns
            : "default";

    public async ValueTask DeleteAsync(ResourceRef resource, bool force = false, CancellationToken ct = default) =>
        await _apply.DeleteAsync(resource, force, ct).ConfigureAwait(false);

    public async ValueTask ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct = default) =>
        await _apply.ScaleAsync(workload, replicas, ct).ConfigureAwait(false);

    public async ValueTask RolloutRestartAsync(ResourceRef workload, CancellationToken ct = default) =>
        await _apply.RolloutRestartAsync(workload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

    // ── Interactive channels (KON-97) ────────────────────────────────────────

    public async ValueTask<IExecSession> StartExecSessionAsync(
        ResourceRef pod, string container, ExecRequest request, CancellationToken ct = default) =>
        await KubernetesExecSession
            .OpenAsync(_client, pod.Name, pod.Namespace, container, request, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Forward a local port. A Service reference is resolved to one of its backing pods first: the
    /// API has no service-level forward, so <c>kubectl port-forward svc/...</c> picks a pod too.
    /// </summary>
    public async ValueTask<IPortForward> PortForwardAsync(
        ResourceRef target, int remotePort, int? localPort = null, CancellationToken ct = default)
    {
        var (pod, port) = target.Kind.Kind == "Service"
            ? await ResolveServiceTargetAsync(target, remotePort, ct).ConfigureAwait(false)
            : (target.Name, remotePort);

        return await KubernetesPortForward
            .StartAsync(_client, pod, target.Namespace, port, localPort, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pick a running pod behind a service, and translate the service port to the container's
    /// target port — forwarding to the service port number would hit the wrong port on the pod.
    /// </summary>
    private async Task<(string Pod, int Port)> ResolveServiceTargetAsync(
        ResourceRef service, int servicePort, CancellationToken ct)
    {
        var live = await _client.CoreV1
            .ReadNamespacedServiceAsync(service.Name, service.Namespace, cancellationToken: ct)
            .ConfigureAwait(false);

        var selector = live.Spec?.Selector;
        if (selector is null || selector.Count == 0)
            throw new InvalidOperationException($"Service {service.Name} selects no pods, so it cannot be forwarded.");

        var label = string.Join(',', selector.Select(s => $"{s.Key}={s.Value}"));
        var pods = await _client.CoreV1
            .ListNamespacedPodAsync(service.Namespace, labelSelector: label, cancellationToken: ct)
            .ConfigureAwait(false);

        var ready = (pods.Items ?? []).FirstOrDefault(p =>
            p.Status?.Phase == "Running" &&
            (p.Status.ContainerStatuses ?? []).All(c => c.Ready));

        if (ready?.Metadata?.Name is not { } name)
            throw new InvalidOperationException($"Service {service.Name} has no ready pod to forward to.");

        // targetPort may be a name rather than a number; fall back to the service port when it is.
        var mapped = live.Spec?.Ports?.FirstOrDefault(p => p.Port == servicePort);
        var target = mapped?.TargetPort?.Value is { } text && int.TryParse(text, out var number)
            ? number
            : servicePort;

        return (name, target);
    }

    public void Dispose() => _client.Dispose();
}
