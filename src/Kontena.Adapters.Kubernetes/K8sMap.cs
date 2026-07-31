using System.Text;
using k8s.Models;
using Kontena.Sdk.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Translates Kubernetes API objects into Kontena's neutral OAL models. This is the whole point of
/// the abstraction layer: everything downstream — grids, detail pages, the apply flow — speaks
/// Kontena's vocabulary, and only this file knows what a <c>V1Deployment</c> looks like.
/// </summary>
internal static class K8sMap
{
    // ── Nodes ────────────────────────────────────────────────────────────────

    public static Node ToNode(V1Node n, NodeUsage? usage, int scheduledPods = 0, long diskCapacityBytes = 0)
    {
        var conditions = n.Status?.Conditions ?? [];
        var ready = conditions.FirstOrDefault(c => c.Type == "Ready");

        return new Node
        {
            Name = n.Metadata?.Name ?? "?",
            Status = ready is null ? "Unknown" : ready.Status switch
            {
                "True" => "Ready",
                "False" => "NotReady",
                _ => "Unknown",
            },
            Roles = [.. RolesOf(n)],
            KubeletVersion = n.Status?.NodeInfo?.KubeletVersion ?? string.Empty,
            OsImage = n.Status?.NodeInfo?.OsImage ?? string.Empty,
            InternalIp = n.Status?.Addresses?.FirstOrDefault(a => a.Type == "InternalIP")?.Address ?? string.Empty,
            Unschedulable = n.Spec?.Unschedulable ?? false,
            Conditions = [.. conditions.Select(ToCondition)],
            Capacity = ToCapacity(n.Status?.Allocatable) with { DiskBytes = diskCapacityBytes },
            Usage = usage,
            ScheduledPods = scheduledPods,
            Age = AgeOf(n.Metadata),
        };
    }

    private static NodeCondition ToCondition(V1NodeCondition c) =>
        new(c.Type, string.Equals(c.Status, "True", StringComparison.Ordinal), c.Reason ?? string.Empty, c.Message ?? string.Empty);

    /// <summary>Roles live in labels — <c>node-role.kubernetes.io/&lt;role&gt;</c>.</summary>
    private static List<string> RolesOf(V1Node n)
    {
        const string prefix = "node-role.kubernetes.io/";
        var roles = (n.Metadata?.Labels ?? new Dictionary<string, string>())
            .Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l.Key[prefix.Length..])
            .Where(r => r.Length > 0)
            .ToList();

        return roles.Count > 0 ? roles : ["worker"];
    }

    private static NodeCapacity ToCapacity(IDictionary<string, ResourceQuantity>? q) => new()
    {
        CpuMillicores = Millicores(q, "cpu"),
        MemoryBytes = Bytes(q, "memory"),
        Pods = (int)Raw(q, "pods"),
    };

    // ── Pods ─────────────────────────────────────────────────────────────────

    public static Pod ToPod(V1Pod p)
    {
        var statuses = p.Status?.ContainerStatuses ?? [];
        var initStatuses = p.Status?.InitContainerStatuses ?? [];
        var ephemeralStatuses = p.Status?.EphemeralContainerStatuses ?? [];

        // Declared ports live on the spec, the rest of the story on the status, and the two are only
        // joined by container name. Building the lookup once keeps that join in one place.
        var ports = PortsByContainer(p.Spec);
        var limits = MemoryLimitsByContainer(p.Spec);

        return new Pod
        {
            Name = p.Metadata?.Name ?? "?",
            Namespace = p.Metadata?.NamespaceProperty ?? "default",
            Phase = p.Status?.Phase switch
            {
                "Running" => PodPhase.Running,
                "Pending" => PodPhase.Pending,
                "Succeeded" => PodPhase.Succeeded,
                "Failed" => PodPhase.Failed,
                _ => PodPhase.Unknown,
            },
            Containers = [.. statuses.Select(c => ToContainerStatus(c, ContainerKind.App, ports, limits))],
            InitContainers = [.. initStatuses.Select(c => ToContainerStatus(c, ContainerKind.Init, ports, limits))],
            EphemeralContainers = [.. ephemeralStatuses.Select(c => ToContainerStatus(c, ContainerKind.Ephemeral, ports, limits))],
            // Init restarts are counted too: a pod that has retried its init container seven times has
            // restarted seven times, and reporting 0 there is the reading that hides the problem.
            Restarts = statuses.Sum(c => c.RestartCount) + initStatuses.Sum(c => c.RestartCount),
            Node = p.Spec?.NodeName ?? string.Empty,
            Ip = p.Status?.PodIP ?? string.Empty,
            Qos = p.Status?.QosClass switch
            {
                "Guaranteed" => QosClass.Guaranteed,
                "Burstable" => QosClass.Burstable,
                _ => QosClass.BestEffort,
            },
            ControlledBy = OwnerOf(p.Metadata),
            Labels = Labels(p.Metadata?.Labels),
            Age = AgeOf(p.Metadata),
        };
    }

    private static Kontena.Sdk.Orchestration.Models.ContainerStatus ToContainerStatus(
        V1ContainerStatus c, ContainerKind kind,
        Dictionary<string, IReadOnlyList<ContainerPort>> ports,
        Dictionary<string, long> memoryLimits) => new()
    {
        Name = c.Name,
        Image = c.Image ?? string.Empty,
        Ready = c.Ready,
        Restarts = c.RestartCount,
        Kind = kind,
        Ports = ports.TryGetValue(c.Name, out var declared) ? declared : [],
        RunState = RunStateOf(c.State),
        Reason = ReasonOf(c.State),
        ExitCode = c.State?.Terminated?.ExitCode,
        // A looping container is *waiting*, so its current state holds nothing about how it died.
        // lastState is the only place that says whether it was killed or exited on its own (KON-150).
        LastTerminationReason = c.LastState?.Terminated?.Reason ?? string.Empty,
        LastExitCode = c.LastState?.Terminated?.ExitCode,
        MemoryLimitBytes = memoryLimits.TryGetValue(c.Name, out var limit) ? limit : null,
    };

    private static ContainerRunState RunStateOf(V1ContainerState? s) => s switch
    {
        { Running: not null } => ContainerRunState.Running,
        { Waiting: not null } => ContainerRunState.Waiting,
        { Terminated: not null } => ContainerRunState.Terminated,
        _ => ContainerRunState.Unknown,
    };

    private static string ReasonOf(V1ContainerState? s) =>
        s?.Waiting?.Reason ?? s?.Terminated?.Reason ?? string.Empty;

    /// <summary>
    /// Declared ports per container name, across init, app and ephemeral containers alike.
    /// <para>
    /// A container may declare none — <c>containerPort</c> is documentation in Kubernetes, not a
    /// binding — so a missing entry means nothing was declared rather than nothing is listening.
    /// </para>
    /// </summary>
    private static Dictionary<string, IReadOnlyList<ContainerPort>> PortsByContainer(V1PodSpec? spec)
    {
        var map = new Dictionary<string, IReadOnlyList<ContainerPort>>(StringComparer.Ordinal);

        foreach (var c in (spec?.InitContainers ?? []).Concat(spec?.Containers ?? []))
            if (c.Ports is { Count: > 0 })
                map[c.Name] = [.. c.Ports.Select(ToContainerPort)];

        foreach (var c in spec?.EphemeralContainers ?? [])
            if (c.Ports is { Count: > 0 })
                map[c.Name] = [.. c.Ports.Select(ToContainerPort)];

        return map;
    }

    /// <summary>
    /// Declared memory limits per container name. A container without one is absent from the map
    /// rather than present with zero: "no limit" and "a limit of nothing" are different answers, and
    /// only the first one is true of an unlimited container.
    /// </summary>
    private static Dictionary<string, long> MemoryLimitsByContainer(V1PodSpec? spec)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var c in (spec?.InitContainers ?? []).Concat(spec?.Containers ?? []))
            if (c.Resources?.Limits is { } limits && limits.TryGetValue("memory", out var quantity))
                map[c.Name] = (long)quantity.ToDouble();

        return map;
    }

    private static ContainerPort ToContainerPort(V1ContainerPort p) =>
        new(p.Name ?? string.Empty, p.ContainerPort, p.Protocol ?? "TCP");

    /// <summary>The controlling owner as "Kind/name" — a ReplicaSet is rolled up to its Deployment.</summary>
    private static string OwnerOf(V1ObjectMeta? meta)
    {
        var owner = meta?.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                    ?? meta?.OwnerReferences?.FirstOrDefault();
        if (owner is null)
            return string.Empty;

        // A pod's owner is usually the ReplicaSet, but users think in Deployments: "web-5f2a8c" is
        // the ReplicaSet for Deployment "web", so trim the generated hash suffix.
        if (owner.Kind == "ReplicaSet")
        {
            var dash = owner.Name.LastIndexOf('-');
            if (dash > 0)
                return $"Deployment/{owner.Name[..dash]}";
        }

        return $"{owner.Kind}/{owner.Name}";
    }

    // ── Workloads ────────────────────────────────────────────────────────────

    public static Workload ToWorkload(V1Deployment d)
    {
        var desired = d.Spec?.Replicas ?? 0;
        var s = d.Status;
        return Workload(d.Metadata, WorkloadKind.Deployment, desired,
            ready: s?.ReadyReplicas ?? 0, upToDate: s?.UpdatedReplicas ?? 0, available: s?.AvailableReplicas ?? 0,
            images: ImagesOf(d.Spec?.Template),
            selector: Labels(d.Spec?.Selector?.MatchLabels),
            strategy: DeploymentStrategy(d.Spec?.Strategy));
    }

    public static Workload ToWorkload(V1StatefulSet s)
    {
        var desired = s.Spec?.Replicas ?? 0;
        var st = s.Status;
        return Workload(s.Metadata, WorkloadKind.StatefulSet, desired,
            ready: st?.ReadyReplicas ?? 0, upToDate: st?.UpdatedReplicas ?? 0, available: st?.AvailableReplicas ?? 0,
            images: ImagesOf(s.Spec?.Template),
            selector: Labels(s.Spec?.Selector?.MatchLabels),
            strategy: s.Spec?.UpdateStrategy?.Type ?? string.Empty);
    }

    public static Workload ToWorkload(V1DaemonSet d)
    {
        var st = d.Status;
        return Workload(d.Metadata, WorkloadKind.DaemonSet, st?.DesiredNumberScheduled ?? 0,
            ready: st?.NumberReady ?? 0, upToDate: st?.UpdatedNumberScheduled ?? 0, available: st?.NumberAvailable ?? 0,
            images: ImagesOf(d.Spec?.Template),
            selector: Labels(d.Spec?.Selector?.MatchLabels),
            strategy: d.Spec?.UpdateStrategy?.Type ?? string.Empty);
    }

    public static Workload ToWorkload(V1Job j)
    {
        var completions = j.Spec?.Completions ?? 1;
        var st = j.Status;
        return Workload(j.Metadata, WorkloadKind.Job, completions,
            ready: st?.Succeeded ?? 0, upToDate: st?.Succeeded ?? 0, available: st?.Ready ?? 0,
            images: ImagesOf(j.Spec?.Template),
            // A Job's selector is generated by the controller (controller-uid), so it is a real
            // selector but never one a user wrote.
            selector: Labels(j.Spec?.Selector?.MatchLabels));
    }

    public static Workload ToWorkload(V1CronJob c) =>
        Workload(c.Metadata, WorkloadKind.CronJob, desired: 0, ready: 0, upToDate: 0, available: 0,
            images: ImagesOf(c.Spec?.JobTemplate?.Spec?.Template),
            schedule: c.Spec?.Schedule ?? string.Empty,
            suspended: c.Spec?.Suspend ?? false,
            // No selector on purpose: a CronJob owns Jobs, not pods. See PodMatching.OwnsPodsDirectly.
            selector: null);

    private static Workload Workload(
        V1ObjectMeta? meta, WorkloadKind kind, int desired, int ready, int upToDate, int available,
        IReadOnlyList<string> images, string schedule = "", bool suspended = false,
        IReadOnlyDictionary<string, string>? selector = null, string strategy = "") => new()
    {
        Name = meta?.Name ?? "?",
        Namespace = meta?.NamespaceProperty ?? "default",
        Kind = kind,
        Ready = ready,
        Desired = desired,
        UpToDate = upToDate,
        Available = available,
        Images = images,
        Schedule = schedule,
        Labels = Labels(meta?.Labels),
        Selector = selector ?? new Dictionary<string, string>(StringComparer.Ordinal),
        Strategy = strategy,
        RolloutStatus = Rollout(kind, desired, ready, upToDate, suspended),
        Age = AgeOf(meta),
    };

    /// <summary>Copies a label map defensively; a null one becomes empty rather than a null reference.</summary>
    private static Dictionary<string, string> Labels(IDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>(StringComparer.Ordinal)
                       : new Dictionary<string, string>(source, StringComparer.Ordinal);

    /// <summary>
    /// "RollingUpdate (max surge 25%, max unavailable 25%)" — the numbers are the point of showing
    /// the strategy at all, since they are what decides how a rollout behaves under pressure.
    /// </summary>
    private static string DeploymentStrategy(V1DeploymentStrategy? s)
    {
        if (s?.Type is not "RollingUpdate")
            return s?.Type ?? string.Empty;

        var surge = s.RollingUpdate?.MaxSurge?.Value;
        var unavailable = s.RollingUpdate?.MaxUnavailable?.Value;

        return surge is null && unavailable is null
            ? "RollingUpdate"
            : $"RollingUpdate (max surge {surge ?? "25%"}, max unavailable {unavailable ?? "25%"})";
    }

    /// <summary>
    /// Summarise rollout health from the replica counts — the same reading <c>kubectl rollout
    /// status</c> gives, minus the conditions detail.
    /// </summary>
    private static RolloutStatus Rollout(WorkloadKind kind, int desired, int ready, int upToDate, bool suspended)
    {
        if (suspended)
            return RolloutStatus.Paused;

        // A CronJob has no replicas to be healthy about; it is fine unless suspended.
        if (kind == WorkloadKind.CronJob)
            return RolloutStatus.Complete;

        if (desired == 0)
            return RolloutStatus.Paused;
        if (ready == 0)
            return RolloutStatus.Degraded;
        if (ready < desired || upToDate < desired)
            return RolloutStatus.Progressing;

        return RolloutStatus.Complete;
    }

    private static IReadOnlyList<string> ImagesOf(V1PodTemplateSpec? template) =>
        [.. (template?.Spec?.Containers ?? []).Select(c => c.Image ?? string.Empty).Where(i => i.Length > 0)];

    // ── Namespaces, services, ingresses, PVCs, events ────────────────────────

    public static KubeNamespace ToNamespace(V1Namespace n) => new()
    {
        Name = n.Metadata?.Name ?? "?",
        Phase = n.Status?.Phase ?? "Active",
        Labels = ReadOnly(n.Metadata?.Labels),
        Age = AgeOf(n.Metadata),
    };

    public static Service ToService(V1Service s)
    {
        var spec = s.Spec;
        var type = spec?.Type switch
        {
            "NodePort" => ServiceType.NodePort,
            "LoadBalancer" => ServiceType.LoadBalancer,
            "ExternalName" => ServiceType.ExternalName,
            _ => ServiceType.ClusterIp,
        };

        // A headless service is a ClusterIP service with no cluster IP — its own kind to Kontena.
        if (type == ServiceType.ClusterIp && spec?.ClusterIP == "None")
            type = ServiceType.Headless;

        return new Service
        {
            Name = s.Metadata?.Name ?? "?",
            Namespace = s.Metadata?.NamespaceProperty ?? "default",
            Type = type,
            ClusterIp = spec?.ClusterIP ?? string.Empty,
            ExternalIp = ExternalIpOf(s),
            Ports = [.. (spec?.Ports ?? []).Select(ToServicePort)],
            Selector = ReadOnly(spec?.Selector),
            Age = AgeOf(s.Metadata),
        };
    }

    private static ServicePort ToServicePort(V1ServicePort p) =>
        new(p.Name ?? "port", p.Port, p.TargetPort?.Value is { } t && int.TryParse(t, out var target) ? target : p.Port,
            p.NodePort, p.Protocol ?? "TCP");

    private static string ExternalIpOf(V1Service s)
    {
        var ingress = s.Status?.LoadBalancer?.Ingress?.FirstOrDefault();
        if (ingress is not null)
            return ingress.Ip ?? ingress.Hostname ?? string.Empty;

        return s.Spec?.ExternalIPs?.FirstOrDefault() ?? string.Empty;
    }

    public static Ingress ToIngress(V1Ingress i) => new()
    {
        Name = i.Metadata?.Name ?? "?",
        Namespace = i.Metadata?.NamespaceProperty ?? "default",
        Class = i.Spec?.IngressClassName ?? string.Empty,
        Rules =
        [
            .. (i.Spec?.Rules ?? []).SelectMany(r =>
                (r.Http?.Paths ?? []).Select(p => new IngressRule(
                    r.Host ?? "*",
                    p.Path ?? "/",
                    p.Backend?.Service?.Name ?? string.Empty,
                    p.Backend?.Service?.Port?.Number ?? 0))),
        ],
        Addresses = [.. (i.Status?.LoadBalancer?.Ingress ?? []).Select(a => a.Ip ?? a.Hostname ?? string.Empty).Where(a => a.Length > 0)],
        TlsHosts = [.. (i.Spec?.Tls ?? []).SelectMany(t => t.Hosts ?? [])],
        Age = AgeOf(i.Metadata),
    };

    public static PersistentVolumeClaim ToPvc(V1PersistentVolumeClaim p) => new()
    {
        Name = p.Metadata?.Name ?? "?",
        Namespace = p.Metadata?.NamespaceProperty ?? "default",
        Phase = p.Status?.Phase switch
        {
            "Bound" => PvcPhase.Bound,
            "Lost" => PvcPhase.Lost,
            _ => PvcPhase.Pending,
        },
        Volume = p.Spec?.VolumeName ?? string.Empty,
        CapacityBytes = Bytes(p.Status?.Capacity, "storage"),
        StorageClass = p.Spec?.StorageClassName ?? string.Empty,
        AccessModes = [.. p.Spec?.AccessModes ?? []],
        Age = AgeOf(p.Metadata),
    };

    public static ClusterEvent ToEvent(Corev1Event e) => new()
    {
        Reason = e.Reason ?? string.Empty,
        Message = e.Message ?? string.Empty,
        Severity = e.Type == "Warning" ? EventSeverity.Warning : EventSeverity.Normal,
        InvolvedObject = ToRef(e.InvolvedObject),
        Source = e.Source?.Component ?? e.ReportingComponent ?? string.Empty,
        Count = e.Count ?? 1,
        LastSeen = EngineTimestamp.From(
            e.LastTimestamp ?? e.EventTime ?? e.FirstTimestamp ?? DateTime.UtcNow),
    };

    private static ResourceRef ToRef(V1ObjectReference? o)
    {
        if (o is null)
            return new ResourceRef(GroupVersionKind.Pod, null, "?");

        var api = o.ApiVersion ?? "v1";
        var slash = api.LastIndexOf('/');
        var gvk = slash < 0
            ? new GroupVersionKind(string.Empty, api, o.Kind ?? "Pod")
            : new GroupVersionKind(api[..slash], api[(slash + 1)..], o.Kind ?? "Pod");

        return new ResourceRef(gvk, o.NamespaceProperty, o.Name ?? "?");
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    public static NodeUsage ToNodeUsage(NodeMetrics m) => new()
    {
        CpuMillicores = Millicores(m.Usage, "cpu"),
        MemoryBytes = Bytes(m.Usage, "memory"),
    };

    public static Kontena.Sdk.Orchestration.Models.PodMetrics ToPodMetrics(k8s.Models.PodMetrics m) => new()
    {
        Pod = m.Metadata?.Name ?? "?",
        Namespace = m.Metadata?.NamespaceProperty ?? "default",
        CpuMillicores = m.Containers?.Sum(c => Millicores(c.Usage, "cpu")) ?? 0,
        MemoryBytes = m.Containers?.Sum(c => Bytes(c.Usage, "memory")) ?? 0,
        Timestamp = EngineTimestamp.From(m.Timestamp ?? DateTime.UtcNow),
    };

    // ── Quantities & ages ────────────────────────────────────────────────────

    /// <summary>CPU quantities are in cores ("250m" = 0.25); Kontena counts milli-cores.</summary>
    private static long Millicores(IDictionary<string, ResourceQuantity>? q, string key) =>
        (long)(Raw(q, key) * 1000m);

    private static long Bytes(IDictionary<string, ResourceQuantity>? q, string key) => (long)Raw(q, key);

    private static decimal Raw(IDictionary<string, ResourceQuantity>? q, string key)
    {
        if (q is null || !q.TryGetValue(key, out var quantity))
            return 0m;

        try
        {
            return quantity.ToDecimal();
        }
        catch (Exception)
        {
            // An unparsable quantity should blank one gauge, not fail the whole listing.
            return 0m;
        }
    }

    /// <summary>
    /// A ConfigMap without its values (KON-249). The client hands over <c>Data</c> and
    /// <c>BinaryData</c> whether or not anyone wants them; only the key names and their sizes
    /// survive this method.
    /// </summary>
    public static ConfigMapSummary ToConfigMap(V1ConfigMap c) => new()
    {
        Name = c.Metadata?.Name ?? "?",
        Namespace = c.Metadata?.NamespaceProperty ?? "default",
        Keys =
        [
            .. Keys(c.Data, v => Encoding.UTF8.GetByteCount(v ?? string.Empty))
                .Concat(Keys(c.BinaryData, v => v?.LongLength ?? 0))
                .OrderBy(k => k.Name, StringComparer.Ordinal),
        ],
        Age = AgeOf(c.Metadata),
    };

    /// <summary>
    /// A Secret without its values. Same shape as <see cref="ToConfigMap"/> and for a stronger
    /// reason: this is the seam where the values the list API sent are dropped.
    /// </summary>
    public static SecretSummary ToSecret(V1Secret s) => new()
    {
        Name = s.Metadata?.Name ?? "?",
        Namespace = s.Metadata?.NamespaceProperty ?? "default",
        Type = string.IsNullOrEmpty(s.Type) ? "Opaque" : s.Type,
        Keys =
        [
            .. Keys(s.Data, v => v?.LongLength ?? 0)
                .OrderBy(k => k.Name, StringComparer.Ordinal),
        ],
        Age = AgeOf(s.Metadata),
    };

    private static IEnumerable<ConfigKey> Keys<T>(IDictionary<string, T>? data, Func<T?, long> size) =>
        data is null ? [] : data.Select(kv => new ConfigKey(kv.Key, size(kv.Value)));

    /// <summary>
    /// The entries of one ConfigMap, decoded. A ConfigMap's <c>data</c> is already text; its
    /// <c>binaryData</c> is bytes and stays bytes.
    /// </summary>
    public static IReadOnlyList<ConfigEntry> ToEntries(V1ConfigMap c) =>
    [
        .. (c.Data ?? new Dictionary<string, string>())
            .Select(kv => Entry(kv.Key, Encoding.UTF8.GetBytes(kv.Value ?? string.Empty)))
            .Concat((c.BinaryData ?? new Dictionary<string, byte[]>()).Select(kv => Entry(kv.Key, kv.Value)))
            .OrderBy(e => e.Key, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The entries of one Secret, decoded. The client has already undone the base64 — <c>Data</c> is
    /// bytes by the time it reaches here — so the only question left is whether those bytes are text.
    /// </summary>
    public static IReadOnlyList<ConfigEntry> ToEntries(V1Secret s) =>
    [
        .. (s.Data ?? new Dictionary<string, byte[]>())
            .Select(kv => Entry(kv.Key, kv.Value))
            .OrderBy(e => e.Key, StringComparer.Ordinal),
    ];

    /// <summary>
    /// One entry from raw bytes. Text only when the bytes decode as UTF-8 without loss: a TLS key
    /// rendered as characters is noise, and a lossy decode would show something that was never in
    /// the secret.
    /// </summary>
    private static ConfigEntry Entry(string key, byte[]? value)
    {
        var bytes = value ?? [];

        string? text = null;
        try
        {
            var decoded = StrictUtf8.GetString(bytes);

            // Decoding is not enough. A PNG's first bytes are 0x00–0x02, which are perfectly valid
            // single-byte UTF-8 and perfectly unreadable — and a value full of NULs is exactly the
            // thing that puts a terminal into a state nobody asked for. Text means it decodes *and*
            // holds no control characters beyond the three that belong in a config file.
            if (!decoded.Any(IsUnprintable))
                text = decoded;
        }
        catch (DecoderFallbackException)
        {
            // Not text. That is an answer, not a failure.
        }

        return new ConfigEntry
        {
            Key = key,
            Text = text,
            Base64 = Convert.ToBase64String(bytes),
            SizeBytes = bytes.LongLength,
        };
    }

    /// <summary>
    /// A character that has no business being rendered: any control character (C0, DEL and C1),
    /// except the tab, newline and carriage return that a script or an nginx.conf legitimately holds.
    /// </summary>
    private static bool IsUnprintable(char c) =>
        char.IsControl(c) && c is not ('\t' or '\n' or '\r');

    /// <summary>UTF-8 that throws rather than substituting replacement characters.</summary>
    private static readonly Encoding StrictUtf8 =
        Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>The client hands back mutable maps; the OAL models expose read-only ones.</summary>
    private static Dictionary<string, string> ReadOnly(IDictionary<string, string>? map) =>
        map is null ? [] : new Dictionary<string, string>(map, StringComparer.Ordinal);

    private static TimeSpan AgeOf(V1ObjectMeta? meta) =>
        meta?.CreationTimestamp is { } created ? DateTime.UtcNow - created.ToUniversalTime() : TimeSpan.Zero;
}
