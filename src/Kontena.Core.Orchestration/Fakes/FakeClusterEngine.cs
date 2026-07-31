using System.Runtime.CompilerServices;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Orchestration;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// An in-memory <see cref="IClusterEngine"/> seeded to match the Kubernetes mockups. It lets us
/// build the whole OAL UI — overview, nodes, namespaces, workloads, pods, services, apply flow —
/// before the real <c>Kontena.Adapters.Kubernetes</c> adapter exists, exactly as
/// <c>FakeEngine</c> did for the CEAL. No cluster, no network; every value is local.
/// </summary>
public sealed class FakeClusterEngine : IClusterEngine, IMetricsAware
{
    private readonly List<KubeContext> _contexts;
    private readonly List<Node> _nodes;
    private readonly List<KubeNamespace> _namespaces;
    private readonly List<Workload> _workloads;
    private readonly List<Pod> _pods;
    private readonly List<Service> _services;
    private readonly List<Ingress> _ingresses;
    private readonly List<PersistentVolumeClaim> _pvcs;
    private readonly List<ConfigMapSummary> _configMaps;
    private readonly List<SecretSummary> _secrets;
    private readonly List<ClusterEvent> _events;

    /// <summary>Applied resources of kinds the fake does not model, kept so apply stays idempotent.</summary>
    private readonly Dictionary<ResourceRef, ManifestDoc> _extras = [];

    private string _activeContext;

    /// <param name="context">Which seeded context to start on; defaults to the first.</param>
    /// <param name="metrics">
    /// Whether a usage backend answers. False is the shape of a fresh kind cluster (KON-93): gauges
    /// unavailable until something installs a metrics-server — which applying its manifest here does,
    /// so the install flow can be driven end to end without a cluster.
    /// </param>
    public FakeClusterEngine(string? context = null, bool metrics = true)
    {
        _capabilities = _capabilities with { Metrics = metrics };

        _contexts =
        [
            new KubeContext { Name = "prod-eu-west", Cluster = "gke_prod", User = "gke-user", Namespace = "default" },
            new KubeContext { Name = "staging", Cluster = "eks_staging", User = "eks-user", Namespace = "default" },
            new KubeContext { Name = "minikube", Cluster = "minikube", User = "minikube", Namespace = "default" },
        ];
        _activeContext = context is not null && _contexts.Any(c => c.Name == context) ? context : _contexts[0].Name;
        for (var i = 0; i < _contexts.Count; i++)
            _contexts[i] = _contexts[i] with { IsCurrent = _contexts[i].Name == _activeContext };

        _nodes =
        [
            Node1("gke-prod-cp-1", ["control-plane"], unschedulable: true),
            // Left behind by a half-finished upgrade: five minors under the apiserver's v1.29, so the
            // version-skew warning (KON-95) has something real to show.
            Node1("gke-prod-worker-1", ["worker"], kubeletVersion: "v1.24.9"),
            // Seeded under disk pressure so the Nodes view's condition indicators have something
            // real to show — a healthy-only seed hides that whole state.
            Node1("gke-prod-worker-2", ["worker"], diskPressure: true),
        ];

        _namespaces =
        [
            Ns("default"), Ns("kube-system"), Ns("ingress-nginx"), Ns("monitoring"), Ns("app"),
        ];

        // Labels and selectors are seeded so the detail pages have something true to show: without
        // them a workload detail lists no pods and a service detail cannot answer "what does this
        // selector reach", which is the one question it exists for (KON-166/167).
        _workloads =
        [
            // Replica counts agree with the seeded pods on purpose. The workload detail shows the
            // breakdown and the matching pods on the same page (KON-166), so a header claiming three
            // above a list of two reads as a bug in Kontena rather than a shortcut in the fake.
            // Deliberately not "all ready everywhere": web is mid-rollout and redis is down, because a
            // healthy-only seed hides the states these pages exist to explain.
            new Workload { Name = "api", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 3, Desired = 3, UpToDate = 3, Available = 3, Images = ["ghcr.io/lionear/api:1.8"], RolloutStatus = RolloutStatus.Complete, Labels = App("api"), Selector = App("api"), Strategy = "RollingUpdate (max surge 25%, max unavailable 25%)", Age = TimeSpan.FromHours(30) },
            new Workload { Name = "web", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 2, Desired = 3, UpToDate = 2, Available = 2, Images = ["nginx:1.27-alpine"], RolloutStatus = RolloutStatus.Progressing, Labels = App("web"), Selector = App("web"), Strategy = "RollingUpdate (max surge 25%, max unavailable 25%)", Age = TimeSpan.FromHours(30) },
            new Workload { Name = "redis", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 0, Desired = 1, UpToDate = 1, Available = 0, Images = ["redis:7-alpine"], RolloutStatus = RolloutStatus.Degraded, Labels = App("redis"), Selector = App("redis"), Strategy = "RollingUpdate (max surge 25%, max unavailable 25%)", Age = TimeSpan.FromHours(6) },
            new Workload { Name = "postgres", Namespace = "app", Kind = WorkloadKind.StatefulSet, Ready = 1, Desired = 1, UpToDate = 1, Available = 1, Images = ["postgres:16"], RolloutStatus = RolloutStatus.Complete, Labels = App("postgres"), Selector = App("postgres"), Strategy = "RollingUpdate", Age = TimeSpan.FromDays(9) },
            new Workload { Name = "node-exporter", Namespace = "monitoring", Kind = WorkloadKind.DaemonSet, Ready = 3, Desired = 3, UpToDate = 3, Available = 3, Images = ["prom/node-exporter:v1.8"], RolloutStatus = RolloutStatus.Complete, Labels = App("node-exporter"), Selector = App("node-exporter"), Strategy = "RollingUpdate", Age = TimeSpan.FromDays(9) },
            // The owner of the wedged migrate pod. Without it that pod is controlled by something that
            // does not appear in the workloads list, and the trail from pod to owner dead-ends.
            new Workload { Name = "migrate", Namespace = "app", Kind = WorkloadKind.Job, Ready = 0, Desired = 1, UpToDate = 0, Available = 0, Images = ["ghcr.io/lionear/migrate:2.1"], RolloutStatus = RolloutStatus.Degraded, Labels = App("migrate"), Selector = App("migrate"), Age = TimeSpan.FromMinutes(6) },
            // No selector: a CronJob owns Jobs, not pods. The detail page says so rather than showing
            // an empty pod list that reads as "none running".
            new Workload { Name = "backup", Namespace = "app", Kind = WorkloadKind.CronJob, Ready = 0, Desired = 0, Images = ["ghcr.io/lionear/backup:2"], Schedule = "0 3 * * *", RolloutStatus = RolloutStatus.Complete, Labels = App("backup"), Age = TimeSpan.FromDays(9) },
        ];

        _pods =
        [
            Pod1("api-7d9c", "app", PodPhase.Running, 2, 0, "gke-prod-worker-1", "Deployment/api", "ghcr.io/lionear/api:1.8"),
            Pod1("api-7d9d", "app", PodPhase.Running, 2, 0, "gke-prod-worker-2", "Deployment/api", "ghcr.io/lionear/api:1.8"),
            Pod1("api-7d9e", "app", PodPhase.Running, 2, 0, "gke-prod-control", "Deployment/api", "ghcr.io/lionear/api:1.8"),
            Pod1("web-5f2a", "app", PodPhase.Running, 1, 0, "gke-prod-worker-1", "Deployment/web", "nginx:1.27-alpine"),
            // web is mid-rollout at 2/3, so two pods and not three — the counts and the list have to
            // tell the same story now that the detail page shows them together.
            Pod1("web-5f2b", "app", PodPhase.Running, 1, 0, "gke-prod-worker-2", "Deployment/web", "nginx:1.27-alpine"),
            new Pod { Name = "redis-0c1e", Namespace = "app", Phase = PodPhase.Pending, Node = "gke-prod-worker-2", Restarts = 7, ControlledBy = "Deployment/redis", Labels = App("redis"), Qos = QosClass.Burstable, Age = TimeSpan.FromMinutes(12), Containers = [new ContainerStatus { Name = "redis", Image = "redis:7-alpine", Ready = false, Restarts = 7, Ports = [new ContainerPort("redis", 6379, "TCP")], RunState = ContainerRunState.Waiting, Reason = "CrashLoopBackOff" }] },
            // A pod wedged on its init container, which is the case the whole of KON-168 is about: the
            // container holding the answer is the one that used to be unreachable. Phase alone reports
            // "Pending" here, indistinguishable from a pod that is merely starting.
            new Pod
            {
                Name = "migrate-9b4f", Namespace = "app", Phase = PodPhase.Pending, Node = "gke-prod-worker-1",
                Restarts = 4, ControlledBy = "Job/migrate", Labels = App("migrate"), Qos = QosClass.Burstable, Age = TimeSpan.FromMinutes(6),
                InitContainers =
                [
                    new ContainerStatus { Name = "wait-for-db", Image = "busybox:1.36", Kind = ContainerKind.Init, Ready = true, RunState = ContainerRunState.Terminated, Reason = "Completed", ExitCode = 0 },
                    new ContainerStatus { Name = "run-migrations", Image = "ghcr.io/lionear/migrate:2.1", Kind = ContainerKind.Init, Restarts = 4, RunState = ContainerRunState.Waiting, Reason = "CrashLoopBackOff" },
                ],
                Containers = [new ContainerStatus { Name = "app", Image = "ghcr.io/lionear/api:1.8", Ports = [new ContainerPort("http", 8080, "TCP")], RunState = ContainerRunState.Waiting, Reason = "PodInitializing" }],
            },
            Pod1("postgres-0", "app", PodPhase.Running, 1, 0, "gke-prod-worker-2", "StatefulSet/postgres", "postgres:16"),
            // One per node, as a DaemonSet gives you — and in the monitoring namespace, so the
            // namespace picker has something to do and the DaemonSet's own detail is not empty.
            Pod1("node-exporter-a1b2", "monitoring", PodPhase.Running, 1, 0, "gke-prod-worker-1", "DaemonSet/node-exporter", "prom/node-exporter:v1.8"),
            Pod1("node-exporter-c3d4", "monitoring", PodPhase.Running, 1, 0, "gke-prod-worker-2", "DaemonSet/node-exporter", "prom/node-exporter:v1.8"),
            Pod1("node-exporter-e5f6", "monitoring", PodPhase.Running, 1, 0, "gke-prod-control", "DaemonSet/node-exporter", "prom/node-exporter:v1.8"),
        ];

        _services =
        [
            new Service { Name = "api", Namespace = "app", Type = ServiceType.ClusterIp, ClusterIp = "10.0.12.4", Ports = [new ServicePort("http", 80, 8080, null, "TCP")], Selector = new Dictionary<string, string> { ["app"] = "api" }, Age = TimeSpan.FromHours(30) },
            new Service { Name = "web", Namespace = "app", Type = ServiceType.LoadBalancer, ClusterIp = "10.0.12.9", ExternalIp = "34.120.55.10", Ports = [new ServicePort("http", 80, 80, 31080, "TCP")], Selector = new Dictionary<string, string> { ["app"] = "web" }, Age = TimeSpan.FromHours(30) },
            new Service { Name = "postgres", Namespace = "app", Type = ServiceType.Headless, ClusterIp = "None", Ports = [new ServicePort("pg", 5432, 5432, null, "TCP")], Selector = new Dictionary<string, string> { ["app"] = "postgres" }, Age = TimeSpan.FromDays(9) },
        ];

        _ingresses =
        [
            new Ingress { Name = "web", Namespace = "app", Class = "nginx", Rules = [new IngressRule("app.example.com", "/", "web", 80)], Addresses = ["34.120.55.10"], TlsHosts = ["app.example.com"], Age = TimeSpan.FromHours(30) },
        ];

        _pvcs =
        [
            new PersistentVolumeClaim { Name = "postgres-data", Namespace = "app", Phase = PvcPhase.Bound, Volume = "pvc-8a1f", CapacityBytes = 20L * 1024 * 1024 * 1024, StorageClass = "standard-rwo", AccessModes = ["RWO"], Age = TimeSpan.FromDays(9) },
        ];

        _configMaps =
        [
            new ConfigMapSummary { Name = "web-config", Namespace = "app", Age = TimeSpan.FromDays(4), Keys = [new ConfigKey("nginx.conf", 812), new ConfigKey("LOG_LEVEL", 4)] },
            new ConfigMapSummary { Name = "kube-root-ca.crt", Namespace = "app", Age = TimeSpan.FromDays(31), Keys = [new ConfigKey("ca.crt", 1099)] },
        ];

        _secrets =
        [
            // Three shapes on purpose: an Opaque secret with text values, a TLS secret whose key is
            // bytes rather than text, and a registry credential.
            new SecretSummary { Name = "postgres-credentials", Namespace = "app", Type = "Opaque", Age = TimeSpan.FromDays(9), Keys = [new ConfigKey("password", 24), new ConfigKey("username", 8)] },
            new SecretSummary { Name = "app-tls", Namespace = "app", Type = "kubernetes.io/tls", Age = TimeSpan.FromDays(2), Keys = [new ConfigKey("tls.crt", 1704), new ConfigKey("tls.key", 1675)] },
            new SecretSummary { Name = "ghcr-pull", Namespace = "app", Type = "kubernetes.io/dockerconfigjson", Age = TimeSpan.FromDays(40), Keys = [new ConfigKey(".dockerconfigjson", 187)] },
        ];

        _configData = new Dictionary<string, IReadOnlyList<ConfigEntry>>(StringComparer.Ordinal)
        {
            ["ConfigMap/app/web-config"] =
            [
                new ConfigEntry { Key = "LOG_LEVEL", Text = "info", SizeBytes = 4 },
                new ConfigEntry { Key = "nginx.conf", Text = "server {\n  listen 80;\n  location / {\n    proxy_pass http://web:8080;\n  }\n}", SizeBytes = 812 },
            ],
            ["Secret/app/postgres-credentials"] =
            [
                new ConfigEntry { Key = "password", Text = "s3cr3t-but-not-really", SizeBytes = 24 },
                new ConfigEntry { Key = "username", Text = "postgres", SizeBytes = 8 },
            ],
            // Text null is what "these bytes are not text" looks like — the case the reveal path has
            // to handle without rendering a terminal full of noise.
            ["Secret/app/app-tls"] =
            [
                new ConfigEntry { Key = "tls.crt", Text = null, SizeBytes = 1704 },
                new ConfigEntry { Key = "tls.key", Text = null, SizeBytes = 1675 },
            ],
        };

        _events =
        [
            new ClusterEvent { Reason = "BackOff", Message = "Back-off restarting failed container redis", Severity = EventSeverity.Warning, InvolvedObject = new ResourceRef(GroupVersionKind.Pod, "app", "redis-0c1e"), Source = "kubelet", Count = 7, LastSeen = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new ClusterEvent { Reason = "ScalingReplicaSet", Message = "Scaled up replica set web-5f2a to 3", Severity = EventSeverity.Normal, InvolvedObject = new ResourceRef(GroupVersionKind.Deployment, "app", "web"), Source = "deployment-controller", Count = 1, LastSeen = DateTimeOffset.UtcNow.AddMinutes(-4) },
        ];
    }

    public string Backend => "kubernetes";

    private ClusterCapabilities _capabilities = new()
    {
        Metrics = true, Exec = true, PortForward = true, Apply = true, Helm = true, Watch = true, Crds = true,
    };

    public ClusterCapabilities Capabilities => _capabilities;

    /// <summary>What answers for usage, so the UI can explain the gauges it is not drawing.</summary>
    public IMetricsSource Metrics => _capabilities.Metrics ? FakeMetricsSource.Instance : NoMetricsSource.Instance;

    public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<BackendInfo>(new ClusterInfo
        {
            Backend = Backend,
            DisplayName = _activeContext,
            Kind = "Kubernetes",
            Version = "v1.29.4",
            Endpoint = "https://34.120.0.1",
            ConnectionState = EngineConnectionState.Connected,
            Distribution = "GKE",
            NodeCount = _nodes.Count,
            Context = _activeContext,
        });

    public ValueTask PingAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public IReadOnlyList<KubeContext> Contexts => _contexts;

    public ValueTask UseContextAsync(string name, CancellationToken ct = default)
    {
        if (_contexts.All(c => c.Name != name))
            throw new ArgumentException($"Unknown context '{name}'.", nameof(name));

        _activeContext = name;
        for (var i = 0; i < _contexts.Count; i++)
            _contexts[i] = _contexts[i] with { IsCurrent = _contexts[i].Name == name };
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ApplyProgress> ApplyAsync(
        ManifestBundle bundle, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var desired in ManifestParser.ParseBundle(bundle.Yaml))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ApplyOne(desired, bundle.DryRun);

            // The install this models is only real once metrics.k8s.io is registered, so that is what
            // flips the capability — not the presence of a Deployment called metrics-server.
            if (!bundle.DryRun
                && desired.Kind == "APIService"
                && desired.Name.Contains("metrics.k8s.io", StringComparison.Ordinal))
            {
                _capabilities = _capabilities with { Metrics = true };
            }
        }
    }

    public ValueTask<string> GetManifestAsync(ResourceRef resource, CancellationToken ct = default)
    {
        var live = Project(resource);
        return ValueTask.FromResult(live is null
            ? $"# {resource} was not found in this cluster."
            : live.ToYaml(includeStatus: true));
    }

    public ValueTask DeleteAsync(ResourceRef resource, bool force = false, CancellationToken ct = default)
    {
        var ns = resource.Namespace;
        var name = resource.Name;
        switch (resource.Kind.Kind)
        {
            case "Pod":
                _pods.RemoveAll(p => p.Name == name && p.Namespace == ns);
                break;
            case "Service":
                _services.RemoveAll(s => s.Name == name && s.Namespace == ns);
                break;
            case "Ingress":
                _ingresses.RemoveAll(i => i.Name == name && i.Namespace == ns);
                break;
            case "PersistentVolumeClaim":
                _pvcs.RemoveAll(p => p.Name == name && p.Namespace == ns);
                break;
            case "ConfigMap":
                _configMaps.RemoveAll(c => c.Name == name && c.Namespace == ns);
                _configData.Remove($"ConfigMap/{ns}/{name}");
                break;
            case "Secret":
                // The values go with the object, which is what makes the delete irreversible and is
                // therefore the thing a fake has to model rather than merely hide the row (KON-253).
                _secrets.RemoveAll(x => x.Name == name && x.Namespace == ns);
                _configData.Remove($"Secret/{ns}/{name}");
                break;
            case "Namespace":
                _namespaces.RemoveAll(n => n.Name == name);
                break;
            default:
                if (ParseWorkloadKind(resource.Kind.Kind) is { } kind)
                {
                    // Deleting a controller takes its pods with it, as the garbage collector would.
                    _workloads.RemoveAll(w => w.Name == name && w.Namespace == ns && w.Kind == kind);
                    _pods.RemoveAll(p => p.Namespace == ns && p.ControlledBy == $"{kind}/{name}");
                }
                else
                {
                    _extras.Remove(resource);
                }

                break;
        }

        return ValueTask.CompletedTask;
    }

    // ── Declarative core (KON-69) ────────────────────────────────────────────

    /// <summary>
    /// Reconcile one parsed document against the seeded world: report what a real apply would do,
    /// with the unified diff a server-side dry-run returns, and persist it unless this is a dry-run.
    /// </summary>
    private ApplyProgress ApplyOne(ManifestDoc desired, bool dryRun)
    {
        if (desired.Error is { } error)
        {
            return new ApplyProgress
            {
                Resource = desired.ToRef(),
                Action = ApplyAction.Failed,
                Error = $"Invalid manifest: {error}.",
            };
        }

        var reference = desired.ToRef();
        var live = Project(reference);

        if (live is null)
        {
            if (!dryRun)
                Store(desired);

            return new ApplyProgress
            {
                Resource = reference,
                Action = dryRun ? ApplyAction.WouldCreate : ApplyAction.Created,
                Diff = ManifestDiff.Compute(string.Empty, desired.ToYaml()),
            };
        }

        // Apply is a merge, not a replace: fields the document leaves out keep their live value,
        // so the diff shows only what the user actually changed.
        var merged = Merge(live, desired);
        var diff = ManifestDiff.Compute(live.ToYaml(), merged.ToYaml());
        if (diff.Length == 0)
            return new ApplyProgress { Resource = reference, Action = ApplyAction.Unchanged };

        if (!dryRun)
            Store(merged);

        return new ApplyProgress
        {
            Resource = reference,
            Action = dryRun ? ApplyAction.WouldChange : ApplyAction.Configured,
            Diff = diff,
        };
    }

    private static ManifestDoc Merge(ManifestDoc live, ManifestDoc desired) => live with
    {
        Replicas = desired.Replicas ?? live.Replicas,
        Schedule = desired.Schedule ?? live.Schedule,
        ServiceType = desired.ServiceType ?? live.ServiceType,
        ClusterIp = desired.ClusterIp ?? live.ClusterIp,
        NodeName = desired.NodeName ?? live.NodeName,
        Labels = MergeMap(live.Labels, desired.Labels),
        Selector = MergeMap(live.Selector, desired.Selector),
        Containers = MergeByName(live.Containers, desired.Containers, c => c.Name),
        Ports = MergeByName(live.Ports, desired.Ports, p => p.Name),
        Raw = desired.Raw ?? live.Raw,
    };

    private static IReadOnlyDictionary<string, string> MergeMap(
        IReadOnlyDictionary<string, string> live, IReadOnlyDictionary<string, string> desired)
    {
        if (desired.Count == 0)
            return live;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in live)
            merged[k] = v;
        foreach (var (k, v) in desired)
            merged[k] = v;
        return merged;
    }

    /// <summary>Strategic-merge semantics for keyed lists: match on name, override, append the rest.</summary>
    private static IReadOnlyList<T> MergeByName<T>(
        IReadOnlyList<T> live, IReadOnlyList<T> desired, Func<T, string> key)
    {
        if (desired.Count == 0)
            return live;

        var merged = new List<T>(live);
        foreach (var item in desired)
        {
            var i = merged.FindIndex(existing => key(existing) == key(item));
            if (i >= 0)
                merged[i] = item;
            else
                merged.Add(item);
        }

        return merged;
    }

    public async IAsyncEnumerable<ResourceEvent> WatchAsync(
        GroupVersionKind kind, string? ns = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Seed the informer with the current world, then complete (a real adapter stays open).
        IEnumerable<ResourceRef> refs = kind.Kind switch
        {
            "Pod" => _pods.Where(p => Match(ns, p.Namespace)).Select(p => new ResourceRef(kind, p.Namespace, p.Name)),
            "Service" => _services.Where(s => Match(ns, s.Namespace)).Select(s => new ResourceRef(kind, s.Namespace, s.Name)),
            "Node" => _nodes.Select(n => new ResourceRef(kind, null, n.Name)),
            "Namespace" => _namespaces.Select(n => new ResourceRef(kind, null, n.Name)),
            _ => _workloads.Where(w => Match(ns, w.Namespace)).Select(w => new ResourceRef(kind, w.Namespace, w.Name)),
        };

        foreach (var r in refs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResourceEvent { Type = WatchEventType.Added, Resource = r };
        }
    }

    public ValueTask<IReadOnlyList<KubeNamespace>> ListNamespacesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<KubeNamespace>>(_namespaces);

    public ValueTask<IReadOnlyList<Node>> ListNodesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Node>>(_nodes);

    public ValueTask<IReadOnlyList<Workload>> ListWorkloadsAsync(
        WorkloadKind? kind = null, string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Workload>>(
            _workloads.Where(w => (kind is null || w.Kind == kind) && Match(ns, w.Namespace)).ToList());

    public ValueTask<IReadOnlyList<Pod>> ListPodsAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Pod>>(_pods.Where(p => Match(ns, p.Namespace)).ToList());

    // ── Generic resources (KON-75) ───────────────────────────────────────────

    /// <summary>
    /// A handful of built-in kinds plus a custom one, because the point of the browser is the kinds
    /// nobody modelled: a fake that only served built-ins would let a UI that cannot show a CRD pass.
    /// </summary>
    private static readonly ApiResource[] Resources =
        [
            new() { Kind = GroupVersionKind.Pod, Plural = "pods", Namespaced = true, Verbs = ["list", "delete"] },
            new() { Kind = GroupVersionKind.Service, Plural = "services", Namespaced = true, Verbs = ["list", "delete"] },
            new() { Kind = GroupVersionKind.Node, Plural = "nodes", Verbs = ["list"] },
            new()
            {
                Kind = new GroupVersionKind(string.Empty, "v1", "ConfigMap"),
                Plural = "configmaps", Namespaced = true, Verbs = ["list", "delete"],
            },
            new()
            {
                Kind = new GroupVersionKind("networking.k8s.io", "v1", "Ingress"),
                Plural = "ingresses", Namespaced = true, Verbs = ["list", "delete"],
            },
            new()
            {
                Kind = new GroupVersionKind("cert-manager.io", "v1", "Certificate"),
                Plural = "certificates", Namespaced = true, Verbs = ["list", "delete"], IsCustom = true,
            },
        ];

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ApiResource>> DiscoverResourcesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ApiResource>>(Resources);

    /// <inheritdoc/>
    public ValueTask<ResourceTable> ListTableAsync(
        GroupVersionKind kind, string? ns = null, CancellationToken ct = default)
    {
        // Columns per kind, the way a server renders them: a browser that drew the same three columns
        // for everything would look right against a fake and wrong against a cluster.
        if (kind.Kind == "Certificate")
        {
            return ValueTask.FromResult(new ResourceTable
            {
                Columns = [new("Name", 0), new("Ready", 0), new("Secret", 0), new("Age", 0)],
                Rows =
                [
                    new(new ResourceRef(kind, ns ?? "default", "kontena-app-tls"),
                        ["kontena-app-tls", "True", "kontena-app-tls", "12d"]),
                    new(new ResourceRef(kind, ns ?? "default", "kontena-api-tls"),
                        ["kontena-api-tls", "False", "kontena-api-tls", "3m"]),
                ],
            });
        }

        var names = kind.Kind switch
        {
            "Pod" => _pods.Where(p => Match(ns, p.Namespace)).Select(p => (p.Name, p.Namespace)).ToArray(),
            "Service" => _services.Where(s => Match(ns, s.Namespace)).Select(s => (s.Name, s.Namespace)).ToArray(),
            "Ingress" => _ingresses.Where(i => Match(ns, i.Namespace)).Select(i => (i.Name, i.Namespace)).ToArray(),
            "Node" => _nodes.Select(n => (n.Name, string.Empty)).ToArray(),
            _ => [],
        };

        return ValueTask.FromResult(new ResourceTable
        {
            Columns = [new("Name", 0), new("Age", 0)],
            Rows =
            [
                .. names.Select(n => new ResourceRow(
                    new ResourceRef(kind, string.IsNullOrEmpty(n.Item2) ? null : n.Item2, n.Item1),
                    [n.Item1, "5d"])),
            ],
        });
    }

    public ValueTask<IReadOnlyList<Service>> ListServicesAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Service>>(_services.Where(s => Match(ns, s.Namespace)).ToList());

    public ValueTask<IReadOnlyList<Ingress>> ListIngressesAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Ingress>>(_ingresses.Where(i => Match(ns, i.Namespace)).ToList());

    public ValueTask<IReadOnlyList<PersistentVolumeClaim>> ListPvcsAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PersistentVolumeClaim>>(_pvcs.Where(p => Match(ns, p.Namespace)).ToList());

    public ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ClusterEvent>>(
            _events.Where(e => Match(ns, e.InvolvedObject.Namespace)).ToList());

    // ── ConfigMaps and Secrets (KON-249) ─────────────────────────────────────

    /// <summary>
    /// The fake's config data, keyed by "kind/namespace/name". Held apart from the summaries for the
    /// same reason the contract splits them: a summary that carried its values would let a page show
    /// a secret it never asked for, and a fake that allowed it would let that mistake pass its tests.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<ConfigEntry>> _configData;

    public ValueTask<IReadOnlyList<ConfigMapSummary>> ListConfigMapsAsync(
        string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ConfigMapSummary>>(
            _configMaps.Where(c => Match(ns, c.Namespace)).ToList());

    public ValueTask<IReadOnlyList<SecretSummary>> ListSecretsAsync(
        string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<SecretSummary>>(
            _secrets.Where(s => Match(ns, s.Namespace)).ToList());

    public ValueTask<IReadOnlyList<ConfigEntry>> GetConfigDataAsync(
        ResourceRef resource, CancellationToken ct = default)
    {
        if (resource.Kind.Kind is not ("Secret" or "ConfigMap"))
            throw new NotSupportedException(
                $"{resource.Kind.Kind} has no configuration data; only ConfigMap and Secret do.");

        var key = $"{resource.Kind.Kind}/{resource.Namespace}/{resource.Name}";
        return ValueTask.FromResult(
            _configData.TryGetValue(key, out var entries) ? entries : []);
    }

    public ValueTask ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct = default)
    {
        var idx = _workloads.FindIndex(w => w.Name == workload.Name && w.Namespace == workload.Namespace);
        if (idx >= 0)
            _workloads[idx] = _workloads[idx] with { Desired = replicas, Ready = Math.Min(_workloads[idx].Ready, replicas) };
        return ValueTask.CompletedTask;
    }

    public ValueTask RolloutRestartAsync(ResourceRef workload, CancellationToken ct = default)
    {
        var idx = _workloads.FindIndex(w => w.Name == workload.Name && w.Namespace == workload.Namespace);
        if (idx >= 0)
            _workloads[idx] = _workloads[idx] with { RolloutStatus = RolloutStatus.Progressing, UpToDate = 0 };
        return ValueTask.CompletedTask;
    }

    public ValueTask<IExecSession> StartExecSessionAsync(
        ResourceRef pod, string container, ExecRequest request, CancellationToken ct = default) =>
        ValueTask.FromResult<IExecSession>(new FakeExecSession(pod.Name, container));

    /// <summary>The handle handed out by the last <see cref="PortForwardAsync"/> — so a caller can
    /// make it drop, which no cluster-less test could otherwise do.</summary>
    public FakePortForward? LastPortForward { get; private set; }

    public ValueTask<IPortForward> PortForwardAsync(
        ResourceRef target, int remotePort, int? localPort = null, CancellationToken ct = default)
    {
        LastPortForward = new FakePortForward(localPort ?? remotePort, remotePort);
        return ValueTask.FromResult<IPortForward>(LastPortForward);
    }

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        ResourceRef pod, string container, bool follow = true, bool previous = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The run that ended is a different story from the one running — that is the whole point of
        // asking for it, so the fake must not answer both with the same lines.
        string[] lines = previous
            ?
            [
                "INFO  starting {container} in {pod}",
                "ERROR could not open database: connection refused",
                "FATAL exiting",
            ]
            :
            [
                "INFO  starting {container} in {pod}",
                "INFO  listening on :8080",
                "INFO  ready",
                "WARN  slow upstream response (412ms)",
            ];
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            var msg = line.Replace("{container}", container).Replace("{pod}", pod.Name);
            var source = msg.StartsWith("ERROR", StringComparison.Ordinal) ? LogSource.Stderr : LogSource.Stdout;
            yield return new LogEntry(DateTimeOffset.UtcNow, source, msg);
        }
    }

    public async IAsyncEnumerable<PodMetrics> StreamMetricsAsync(
        ResourceRef pod, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new PodMetrics
            {
                Pod = pod.Name,
                Namespace = pod.Namespace ?? "default",
                CpuMillicores = 40 + i * 12,
                MemoryBytes = (128 + i * 4) * 1024L * 1024,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }
    }

    public async IAsyncEnumerable<ResourceEvent> StreamEventsAsync(
        string? ns = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in _events.Where(e => Match(ns, e.InvolvedObject.Namespace)))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResourceEvent { Type = WatchEventType.Modified, Resource = e.InvolvedObject };
        }
    }

    // ── Projection: seeded world ⇄ manifests ─────────────────────────────────

    /// <summary>
    /// Read a live resource as a manifest, or null when it does not exist. Kinds the fake models
    /// come from the seeded lists; anything else (an HPA, a ConfigMap) is served from
    /// <see cref="_extras"/>, which holds whatever was applied to it verbatim.
    /// </summary>
    private ManifestDoc? Project(ResourceRef resource)
    {
        var ns = resource.Namespace;
        var name = resource.Name;

        switch (resource.Kind.Kind)
        {
            case "Pod":
                return _pods.Find(p => p.Name == name && p.Namespace == ns) is { } pod ? ToDoc(pod) : null;

            case "Service":
                return _services.Find(s => s.Name == name && s.Namespace == ns) is { } svc ? ToDoc(svc) : null;

            case "Namespace":
                return _namespaces.Find(n => n.Name == name) is { } kubeNs ? ToDoc(kubeNs) : null;

            case "Node":
                return _nodes.Find(n => n.Name == name) is { } node ? ToDoc(node) : null;

            case "Ingress":
                return _ingresses.Find(i => i.Name == name && i.Namespace == ns) is { } ing ? ToDoc(ing) : null;

            case "PersistentVolumeClaim":
                return _pvcs.Find(p => p.Name == name && p.Namespace == ns) is { } pvc ? ToDoc(pvc) : null;

            default:
                if (ParseWorkloadKind(resource.Kind.Kind) is { } kind)
                {
                    return _workloads.Find(w => w.Name == name && w.Namespace == ns && w.Kind == kind) is { } workload
                        ? ToDoc(workload)
                        : null;
                }

                return _extras.GetValueOrDefault(resource);
        }
    }

    /// <summary>Write an applied manifest back into the seeded world.</summary>
    private void Store(ManifestDoc doc)
    {
        var reference = doc.ToRef();
        var ns = doc.Namespace;

        switch (doc.Kind)
        {
            case "Pod":
            {
                var i = _pods.FindIndex(p => p.Name == doc.Name && p.Namespace == ns);
                var containers = doc.Containers
                    .Select(c => new ContainerStatus { Name = c.Name, Image = c.Image, Ready = true, RunState = ContainerRunState.Running })
                    .ToList();
                var pod = i >= 0
                    ? _pods[i] with { Node = doc.NodeName ?? _pods[i].Node, Containers = containers.Count > 0 ? containers : _pods[i].Containers }
                    : new Pod { Name = doc.Name, Namespace = ns ?? "default", Phase = PodPhase.Pending, Node = doc.NodeName ?? string.Empty, ControlledBy = string.Empty, Containers = containers, Age = TimeSpan.Zero };

                if (i >= 0)
                    _pods[i] = pod;
                else
                    _pods.Add(pod);
                break;
            }

            case "Service":
            {
                var i = _services.FindIndex(s => s.Name == doc.Name && s.Namespace == ns);
                var ports = doc.Ports.Select(p => new ServicePort(p.Name, p.Port, p.TargetPort, null, p.Protocol)).ToList();
                var service = i >= 0
                    ? _services[i] with { Type = ParseServiceType(doc.ServiceType) ?? _services[i].Type, Selector = doc.Selector, Ports = ports.Count > 0 ? ports : _services[i].Ports }
                    : new Service { Name = doc.Name, Namespace = ns ?? "default", Type = ParseServiceType(doc.ServiceType) ?? ServiceType.ClusterIp, ClusterIp = doc.ClusterIp ?? "10.0.0.1", Selector = doc.Selector, Ports = ports, Age = TimeSpan.Zero };

                if (i >= 0)
                    _services[i] = service;
                else
                    _services.Add(service);
                break;
            }

            case "Namespace":
            {
                if (!_namespaces.Exists(n => n.Name == doc.Name))
                    _namespaces.Add(new KubeNamespace { Name = doc.Name, Phase = "Active", Labels = doc.Labels, Age = TimeSpan.Zero });
                break;
            }

            default:
            {
                if (ParseWorkloadKind(doc.Kind) is not { } kind)
                {
                    // Not a kind the fake models — keep the document so a repeat apply is a no-op.
                    _extras[reference] = doc;
                    break;
                }

                var i = _workloads.FindIndex(w => w.Name == doc.Name && w.Namespace == ns && w.Kind == kind);
                var images = doc.Containers.Select(c => c.Image).ToList();
                if (i >= 0)
                {
                    var live = _workloads[i];
                    var desired = doc.Replicas ?? live.Desired;
                    _workloads[i] = live with
                    {
                        Desired = desired,
                        Ready = Math.Min(live.Ready, desired),
                        UpToDate = 0,
                        Images = images.Count > 0 ? images : live.Images,
                        Schedule = doc.Schedule ?? live.Schedule,
                        RolloutStatus = RolloutStatus.Progressing,
                    };
                }
                else
                {
                    _workloads.Add(new Workload
                    {
                        Name = doc.Name,
                        Namespace = ns ?? "default",
                        Kind = kind,
                        Desired = doc.Replicas ?? 1,
                        Ready = 0,
                        UpToDate = 0,
                        Available = 0,
                        Images = images,
                        Schedule = doc.Schedule ?? string.Empty,
                        RolloutStatus = RolloutStatus.Progressing,
                        Age = TimeSpan.Zero,
                    });
                }

                break;
            }
        }
    }

    private static ManifestDoc ToDoc(Workload w) => new()
    {
        ApiVersion = w.Kind is WorkloadKind.CronJob or WorkloadKind.Job ? "batch/v1" : "apps/v1",
        Kind = w.Kind.ToString(),
        Name = w.Name,
        Namespace = w.Namespace,
        Replicas = w.IsScalable ? w.Desired : null,
        Schedule = string.IsNullOrEmpty(w.Schedule) ? null : w.Schedule,
        Selector = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = w.Name },
        Containers = w.Images.Select((image, i) => new ManifestContainer(i == 0 ? w.Name : $"{w.Name}-{i}", image)).ToList(),
        Status = [$"readyReplicas: {w.Ready}", $"updatedReplicas: {w.UpToDate}", $"availableReplicas: {w.Available}"],
    };

    private static ManifestDoc ToDoc(Pod p) => new()
    {
        Kind = "Pod",
        Name = p.Name,
        Namespace = p.Namespace,
        NodeName = string.IsNullOrEmpty(p.Node) ? null : p.Node,
        Containers = p.Containers.Select(c => new ManifestContainer(c.Name, c.Image)).ToList(),
        Status = [$"phase: {p.Phase}", $"podIP: {p.Ip}", $"qosClass: {p.Qos}"],
    };

    private static ManifestDoc ToDoc(Service s) => new()
    {
        Kind = "Service",
        Name = s.Name,
        Namespace = s.Namespace,
        ServiceType = ServiceTypeName(s.Type),
        ClusterIp = string.IsNullOrEmpty(s.ClusterIp) ? null : s.ClusterIp,
        Selector = s.Selector,
        Ports = s.Ports.Select(p => new ManifestPort(p.Name, p.Port, p.TargetPort, p.Protocol)).ToList(),
        Status = string.IsNullOrEmpty(s.ExternalIp) ? [] : [$"loadBalancer: {s.ExternalIp}"],
    };

    private static ManifestDoc ToDoc(KubeNamespace n) => new()
    {
        Kind = "Namespace", Name = n.Name, Labels = n.Labels, Status = [$"phase: {n.Phase}"],
    };

    private static ManifestDoc ToDoc(Node n) => new()
    {
        Kind = "Node", Name = n.Name, Status = [$"nodeInfo: {n.KubeletVersion}", $"status: {n.Status}"],
    };

    private static ManifestDoc ToDoc(Ingress i) => new()
    {
        ApiVersion = "networking.k8s.io/v1",
        Kind = "Ingress",
        Name = i.Name,
        Namespace = i.Namespace,
        Status = i.Addresses.Count == 0 ? [] : [$"loadBalancer: {i.Addresses[0]}"],
    };

    private static ManifestDoc ToDoc(PersistentVolumeClaim p) => new()
    {
        Kind = "PersistentVolumeClaim",
        Name = p.Name,
        Namespace = p.Namespace,
        Status = [$"phase: {p.Phase}", $"volumeName: {p.Volume}"],
    };

    private static WorkloadKind? ParseWorkloadKind(string kind) =>
        Enum.TryParse<WorkloadKind>(kind, out var parsed) ? parsed : null;

    private static ServiceType? ParseServiceType(string? type) => type switch
    {
        "ClusterIP" => ServiceType.ClusterIp,
        "NodePort" => ServiceType.NodePort,
        "LoadBalancer" => ServiceType.LoadBalancer,
        "ExternalName" => ServiceType.ExternalName,
        _ => null,
    };

    private static string ServiceTypeName(ServiceType type) => type switch
    {
        ServiceType.NodePort => "NodePort",
        ServiceType.LoadBalancer => "LoadBalancer",
        ServiceType.ExternalName => "ExternalName",
        _ => "ClusterIP",
    };

    private static bool Match(string? filter, string? value) =>
        filter is null || string.Equals(filter, value, StringComparison.Ordinal);

    private static KubeNamespace Ns(string name) =>
        new() { Name = name, Phase = "Active", Age = TimeSpan.FromDays(9) };

    private static Node Node1(
        string name, IReadOnlyList<string> roles, bool unschedulable = false, bool diskPressure = false,
        string kubeletVersion = "v1.29.4") => new()
    {
        Name = name,
        Status = "Ready",
        Roles = roles,
        KubeletVersion = kubeletVersion,
        OsImage = "Container-Optimized OS",
        InternalIp = "10.128.0." + (name.GetHashCode() & 0x3f),
        Unschedulable = unschedulable,
        Conditions =
        [
            new NodeCondition("Ready", true, "KubeletReady", "kubelet is posting ready status"),
            new NodeCondition("MemoryPressure", false, "KubeletHasSufficientMemory", string.Empty),
            new NodeCondition("DiskPressure", diskPressure, diskPressure ? "KubeletHasDiskPressure" : "KubeletHasNoDiskPressure",
                diskPressure ? "kubelet has disk pressure" : string.Empty),
            new NodeCondition("PIDPressure", false, "KubeletHasSufficientPID", string.Empty),
            new NodeCondition("NetworkUnavailable", false, "RouteCreated", string.Empty),
        ],
        Capacity = new NodeCapacity { CpuMillicores = 4000, MemoryBytes = 16L * 1024 * 1024 * 1024, Pods = 110 },
        Usage = new NodeUsage { CpuMillicores = 1200, MemoryBytes = 6L * 1024 * 1024 * 1024 },
        ScheduledPods = 24,
        Age = TimeSpan.FromDays(9),
    };

    /// <summary>
    /// The port a fake container declares. Real workloads declare one and the port-forward dialog reads
    /// them (KON-170); a fake that declares none would show an empty picker and quietly suggest that
    /// pods do not have ports.
    /// </summary>
    private static IReadOnlyList<ContainerPort> PortsFor(string image) => image switch
    {
        var i when i.Contains("nginx", StringComparison.Ordinal) => [new ContainerPort("http", 80, "TCP")],
        var i when i.Contains("postgres", StringComparison.Ordinal) => [new ContainerPort("pg", 5432, "TCP")],
        var i when i.Contains("redis", StringComparison.Ordinal) => [new ContainerPort("redis", 6379, "TCP")],
        _ => [new ContainerPort("http", 8080, "TCP"), new ContainerPort("metrics", 9090, "TCP")],
    };

    /// <summary>The one-label convention the seeded workloads and services agree on: <c>app=&lt;name&gt;</c>.</summary>
    private static Dictionary<string, string> App(string name) =>
        new(StringComparer.Ordinal) { ["app"] = name };

    private static Pod Pod1(string name, string ns, PodPhase phase, int containers, int restarts, string node, string owner, string image) => new()
    {
        // Pods carry the label their owner selects on, so ownership and selector matching agree —
        // which is what makes the two detail pages tell the same story about the same pod.
        Labels = App(owner.Contains('/', StringComparison.Ordinal) ? owner.Split('/')[1] : owner),
        Name = name,
        Namespace = ns,
        Phase = phase,
        Node = node,
        Ip = "10.4.1." + (name.GetHashCode() & 0x3f),
        Restarts = restarts,
        ControlledBy = owner,
        Qos = QosClass.Burstable,
        Age = TimeSpan.FromHours(30),
        Containers = Enumerable.Range(0, containers)
            .Select(i => new ContainerStatus
            {
                Name = containers == 1 ? name.Split('-')[0] : $"c{i}",
                Image = image,
                Ready = phase == PodPhase.Running,
                Restarts = restarts,
                Ports = PortsFor(image),
                RunState = phase == PodPhase.Running ? ContainerRunState.Running : ContainerRunState.Waiting,
                Reason = phase == PodPhase.Running ? string.Empty : phase.ToString(),
            })
            .ToList(),
        // Every one of these pods ran an init container to get here, and a fake that leaves them out
        // makes the container picker look like a list of one thing (KON-168).
        InitContainers =
        [
            new ContainerStatus
            {
                Name = "wait-for-db",
                Image = "busybox:1.36",
                Kind = ContainerKind.Init,
                Ready = true,
                RunState = ContainerRunState.Terminated,
                Reason = "Completed",
                ExitCode = 0,
            },
        ],
    };
}

/// <summary>A no-op <see cref="IPortForward"/> handle for the fake cluster.</summary>
public sealed class FakePortForward(int localPort, int remotePort) : IPortForward
{
    public int LocalPort { get; } = localPort;
    public int RemotePort { get; } = remotePort;
    public bool IsActive { get; private set; } = true;

    public event Action<string>? Closed;

    /// <summary>
    /// Make the tunnel fall over the way a real one does — the only way to reach that state without a
    /// cluster, and the fake is what the Port forwards page is built against.
    /// </summary>
    public void Drop(string reason = "The pod is gone.")
    {
        if (!IsActive)
            return;

        IsActive = false;
        Closed?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        // Disposal is a stop, not a drop: no Closed, per the contract.
        IsActive = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A usage backend that answers, for the fake cluster. Only its name matters here — the numbers the
/// gauges draw come from <see cref="FakeClusterEngine"/>'s own node and pod listings.
/// </summary>
internal sealed class FakeMetricsSource : IMetricsSource
{
    public static readonly FakeMetricsSource Instance = new();

    public string Name => "metrics-server";

    public bool IsAvailable => true;

    public ValueTask<bool> ProbeAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, NodeUsage>>(new Dictionary<string, NodeUsage>());

    public ValueTask<IReadOnlyList<PodMetrics>> GetPodUsageAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PodMetrics>>([]);
}
