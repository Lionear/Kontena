using System.Runtime.CompilerServices;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Models;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// An in-memory <see cref="IClusterEngine"/> seeded to match the Kubernetes mockups. It lets us
/// build the whole OAL UI — overview, nodes, namespaces, workloads, pods, services, apply flow —
/// before the real <c>Kontena.Adapters.Kubernetes</c> adapter exists, exactly as
/// <c>FakeEngine</c> did for the CEAL. No cluster, no network; every value is local.
/// </summary>
public sealed class FakeClusterEngine : IClusterEngine
{
    private readonly List<KubeContext> _contexts;
    private readonly List<Node> _nodes;
    private readonly List<KubeNamespace> _namespaces;
    private readonly List<Workload> _workloads;
    private readonly List<Pod> _pods;
    private readonly List<Service> _services;
    private readonly List<Ingress> _ingresses;
    private readonly List<PersistentVolumeClaim> _pvcs;
    private readonly List<ClusterEvent> _events;

    private string _activeContext;

    public FakeClusterEngine()
    {
        _contexts =
        [
            new KubeContext { Name = "prod-eu-west", Cluster = "gke_prod", User = "gke-user", Namespace = "default", IsCurrent = true },
            new KubeContext { Name = "staging", Cluster = "eks_staging", User = "eks-user", Namespace = "default" },
            new KubeContext { Name = "minikube", Cluster = "minikube", User = "minikube", Namespace = "default" },
        ];
        _activeContext = _contexts[0].Name;

        _nodes =
        [
            Node1("gke-prod-cp-1", ["control-plane"], unschedulable: true),
            Node1("gke-prod-worker-1", ["worker"]),
            Node1("gke-prod-worker-2", ["worker"]),
        ];

        _namespaces =
        [
            Ns("default"), Ns("kube-system"), Ns("ingress-nginx"), Ns("monitoring"), Ns("app"),
        ];

        _workloads =
        [
            new Workload { Name = "api", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 3, Desired = 3, UpToDate = 3, Available = 3, Images = ["ghcr.io/lionear/api:1.8"], RolloutStatus = RolloutStatus.Complete, Age = TimeSpan.FromHours(30) },
            new Workload { Name = "web", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 2, Desired = 3, UpToDate = 2, Available = 2, Images = ["nginx:1.27-alpine"], RolloutStatus = RolloutStatus.Progressing, Age = TimeSpan.FromHours(30) },
            new Workload { Name = "redis", Namespace = "app", Kind = WorkloadKind.Deployment, Ready = 0, Desired = 1, UpToDate = 1, Available = 0, Images = ["redis:7-alpine"], RolloutStatus = RolloutStatus.Degraded, Age = TimeSpan.FromHours(6) },
            new Workload { Name = "postgres", Namespace = "app", Kind = WorkloadKind.StatefulSet, Ready = 1, Desired = 1, UpToDate = 1, Available = 1, Images = ["postgres:16"], RolloutStatus = RolloutStatus.Complete, Age = TimeSpan.FromDays(9) },
            new Workload { Name = "node-exporter", Namespace = "monitoring", Kind = WorkloadKind.DaemonSet, Ready = 3, Desired = 3, UpToDate = 3, Available = 3, Images = ["prom/node-exporter:v1.8"], RolloutStatus = RolloutStatus.Complete, Age = TimeSpan.FromDays(9) },
            new Workload { Name = "backup", Namespace = "app", Kind = WorkloadKind.CronJob, Ready = 0, Desired = 0, Images = ["ghcr.io/lionear/backup:2"], Schedule = "0 3 * * *", RolloutStatus = RolloutStatus.Complete, Age = TimeSpan.FromDays(9) },
        ];

        _pods =
        [
            Pod1("api-7d9c", "app", PodPhase.Running, 2, 0, "gke-prod-worker-1", "Deployment/api", "ghcr.io/lionear/api:1.8"),
            Pod1("api-7d9d", "app", PodPhase.Running, 2, 0, "gke-prod-worker-2", "Deployment/api", "ghcr.io/lionear/api:1.8"),
            Pod1("web-5f2a", "app", PodPhase.Running, 1, 0, "gke-prod-worker-1", "Deployment/web", "nginx:1.27-alpine"),
            new Pod { Name = "redis-0c1e", Namespace = "app", Phase = PodPhase.Pending, Node = "gke-prod-worker-2", Restarts = 7, ControlledBy = "Deployment/redis", Qos = QosClass.Burstable, Age = TimeSpan.FromMinutes(12), Containers = [new ContainerStatus { Name = "redis", Image = "redis:7-alpine", Ready = false, Restarts = 7, State = "Waiting: CrashLoopBackOff" }] },
            Pod1("postgres-0", "app", PodPhase.Running, 1, 0, "gke-prod-worker-2", "StatefulSet/postgres", "postgres:16"),
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

        _events =
        [
            new ClusterEvent { Reason = "BackOff", Message = "Back-off restarting failed container redis", Severity = EventSeverity.Warning, InvolvedObject = new ResourceRef(GroupVersionKind.Pod, "app", "redis-0c1e"), Source = "kubelet", Count = 7, LastSeen = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new ClusterEvent { Reason = "ScalingReplicaSet", Message = "Scaled up replica set web-5f2a to 3", Severity = EventSeverity.Normal, InvolvedObject = new ResourceRef(GroupVersionKind.Deployment, "app", "web"), Source = "deployment-controller", Count = 1, LastSeen = DateTimeOffset.UtcNow.AddMinutes(-4) },
        ];
    }

    public string Backend => "kubernetes";

    public ClusterCapabilities Capabilities { get; } = new()
    {
        Metrics = true, Exec = true, PortForward = true, Apply = true, Helm = true, Watch = true, Crds = true,
    };

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
        var docs = bundle.Yaml.Split("\n---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var i = 0;
        foreach (var _ in docs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            var reference = new ResourceRef(GroupVersionKind.Deployment, "app", $"applied-{i++}");
            yield return new ApplyProgress
            {
                Resource = reference,
                Action = bundle.DryRun ? ApplyAction.WouldChange : ApplyAction.Configured,
                Diff = bundle.DryRun ? "  spec:\n-   replicas: 2\n+   replicas: 3" : string.Empty,
            };
        }
    }

    public ValueTask<string> GetManifestAsync(ResourceRef resource, CancellationToken ct = default) =>
        ValueTask.FromResult(
            $"apiVersion: {(resource.Kind.IsCoreGroup ? resource.Kind.Version : $"{resource.Kind.Group}/{resource.Kind.Version}")}\n" +
            $"kind: {resource.Kind.Kind}\n" +
            $"metadata:\n  name: {resource.Name}\n" +
            (resource.Namespace is null ? string.Empty : $"  namespace: {resource.Namespace}\n"));

    public ValueTask DeleteAsync(ResourceRef resource, bool force = false, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

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

    public ValueTask<IReadOnlyList<Service>> ListServicesAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Service>>(_services.Where(s => Match(ns, s.Namespace)).ToList());

    public ValueTask<IReadOnlyList<Ingress>> ListIngressesAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<Ingress>>(_ingresses.Where(i => Match(ns, i.Namespace)).ToList());

    public ValueTask<IReadOnlyList<PersistentVolumeClaim>> ListPvcsAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PersistentVolumeClaim>>(_pvcs.Where(p => Match(ns, p.Namespace)).ToList());

    public ValueTask<IReadOnlyList<ClusterEvent>> ListEventsAsync(string? ns = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ClusterEvent>>(
            _events.Where(e => Match(ns, e.InvolvedObject.Namespace)).ToList());

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

    public ValueTask<IPortForward> PortForwardAsync(
        ResourceRef target, int remotePort, int? localPort = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IPortForward>(new FakePortForward(localPort ?? remotePort, remotePort));

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        ResourceRef pod, string container, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string[] lines =
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

    private static bool Match(string? filter, string? value) =>
        filter is null || string.Equals(filter, value, StringComparison.Ordinal);

    private static KubeNamespace Ns(string name) =>
        new() { Name = name, Phase = "Active", Age = TimeSpan.FromDays(9) };

    private static Node Node1(string name, IReadOnlyList<string> roles, bool unschedulable = false) => new()
    {
        Name = name,
        Status = "Ready",
        Roles = roles,
        KubeletVersion = "v1.29.4",
        OsImage = "Container-Optimized OS",
        InternalIp = "10.128.0." + (name.GetHashCode() & 0x3f),
        Unschedulable = unschedulable,
        Capacity = new NodeCapacity { CpuMillicores = 4000, MemoryBytes = 16L * 1024 * 1024 * 1024, Pods = 110 },
        Usage = new NodeUsage { CpuMillicores = 1200, MemoryBytes = 6L * 1024 * 1024 * 1024, Pods = 24 },
        Age = TimeSpan.FromDays(9),
    };

    private static Pod Pod1(string name, string ns, PodPhase phase, int containers, int restarts, string node, string owner, string image) => new()
    {
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
            .Select(i => new ContainerStatus { Name = containers == 1 ? name.Split('-')[0] : $"c{i}", Image = image, Ready = phase == PodPhase.Running, Restarts = restarts, State = phase == PodPhase.Running ? "Running" : phase.ToString() })
            .ToList(),
    };
}

/// <summary>A no-op <see cref="IPortForward"/> handle for the fake cluster.</summary>
internal sealed class FakePortForward(int localPort, int remotePort) : IPortForward
{
    public int LocalPort { get; } = localPort;
    public int RemotePort { get; } = remotePort;
    public bool IsActive { get; private set; } = true;

    public ValueTask DisposeAsync()
    {
        IsActive = false;
        return ValueTask.CompletedTask;
    }
}
