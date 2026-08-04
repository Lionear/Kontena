namespace Kontena.Sdk.Orchestration.Models;

/// <summary>A pod's lifecycle phase.</summary>
public enum PodPhase
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Unknown,
}

/// <summary>Quality-of-service class the scheduler assigned.</summary>
public enum QosClass
{
    BestEffort,
    Burstable,
    Guaranteed,
}

/// <summary>
/// What sort of container this is. Init containers are not app containers that happen to run early:
/// they run in sequence before the rest start, and finishing is their success condition rather than a
/// failure — which is why "Ready" reads differently for them (KON-168).
/// </summary>
public enum ContainerKind
{
    Init,
    App,
    Ephemeral,
}

/// <summary>Which of the three container states the kubelet reports.</summary>
public enum ContainerRunState
{
    Unknown,
    Waiting,
    Running,
    Terminated,
}

/// <summary>
/// A port a container declares. Kubernetes treats these as documentation — traffic reaches a port
/// whether or not it is listed — so an empty list means "nothing declared", never "nothing listening".
/// </summary>
public readonly record struct ContainerPort(string Name, int Number, string Protocol);

/// <summary>Per-container status inside a pod — drives the pod-detail container list.</summary>
public sealed record ContainerStatus
{
    public required string Name { get; init; }

    /// <summary>Image the container runs.</summary>
    public string Image { get; init; } = string.Empty;

    /// <summary>Whether the container currently passes its readiness probe.</summary>
    public bool Ready { get; init; }

    /// <summary>Restart count.</summary>
    public int Restarts { get; init; }

    /// <summary>Init, app or ephemeral (KON-168).</summary>
    public ContainerKind Kind { get; init; } = ContainerKind.App;

    /// <summary>Ports the container declares, for the port-forward dialog to offer (KON-170).</summary>
    public IReadOnlyList<ContainerPort> Ports { get; init; } = [];

    public ContainerRunState RunState { get; init; } = ContainerRunState.Unknown;

    /// <summary>Raw kubelet reason — "CrashLoopBackOff", "Completed", "Error". Empty while running.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Exit code once terminated.</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Why the previous run of this container ended, when there was one — the kubelet's
    /// <c>lastState.terminated.reason</c>, e.g. "OOMKilled" or "Error". A container in
    /// CrashLoopBackOff is waiting, so its current state says nothing about how it died; this is the
    /// only field that does.
    /// </summary>
    public string LastTerminationReason { get; init; } = string.Empty;

    /// <summary>Exit code of the previous run, when there was one.</summary>
    public int? LastExitCode { get; init; }

    /// <summary>
    /// Memory limit from the pod spec in bytes, when one is declared. Null means unlimited, which is
    /// a different answer than zero and changes what an OOM kill means.
    /// </summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>
    /// State summary, e.g. "Running", "Waiting: CrashLoopBackOff", "Terminated: Error". Derived rather
    /// than stored so the display string and the fields it summarises cannot disagree.
    /// </summary>
    public string State => RunState switch
    {
        ContainerRunState.Running => "Running",
        ContainerRunState.Waiting => Reason.Length == 0 ? "Waiting" : $"Waiting: {Reason}",
        ContainerRunState.Terminated => Reason.Length == 0 ? "Terminated" : $"Terminated: {Reason}",
        _ => string.Empty,
    };

    /// <summary>An init container that finished as it should — the only success it has.</summary>
    public bool CompletedSuccessfully => RunState == ContainerRunState.Terminated && ExitCode is null or 0;

    /// <summary>Whether a shell can be opened here. A finished init container has nothing left to exec into.</summary>
    public bool CanExec => RunState == ContainerRunState.Running;
}

/// <summary>A neutral view of a pod, seeded richly enough to build the pods grid and detail.</summary>
public sealed record Pod
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public PodPhase Phase { get; init; } = PodPhase.Unknown;

    /// <summary>
    /// The app containers and their per-container status. Deliberately excludes init and ephemeral
    /// containers so that <see cref="ReadyContainers"/> and the "2/2" column keep counting the same
    /// thing <c>kubectl get pods</c> does.
    /// </summary>
    public IReadOnlyList<ContainerStatus> Containers { get; init; } = [];

    /// <summary>Init containers, in the order they run (KON-168).</summary>
    public IReadOnlyList<ContainerStatus> InitContainers { get; init; } = [];

    /// <summary>Ephemeral debug containers, if any were attached.</summary>
    public IReadOnlyList<ContainerStatus> EphemeralContainers { get; init; } = [];

    /// <summary>
    /// Every container in the pod in the order they matter: init first, then app, then ephemeral.
    /// This is what a container picker should offer — a pod stuck in <c>Init:CrashLoopBackOff</c> is
    /// exactly when you want the init container's logs, and it is the one you could not choose before.
    /// </summary>
    public IReadOnlyList<ContainerStatus> AllContainers =>
        [.. InitContainers, .. Containers, .. EphemeralContainers];

    /// <summary>Total restarts across all containers.</summary>
    public int Restarts { get; init; }

    /// <summary>Node the pod is scheduled on.</summary>
    public string Node { get; init; } = string.Empty;

    /// <summary>Pod IP, when assigned.</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>Scheduler QoS class.</summary>
    public QosClass Qos { get; init; } = QosClass.BestEffort;

    /// <summary>Owning controller, e.g. "Deployment/api" — empty for bare pods.</summary>
    public string ControlledBy { get; init; } = string.Empty;

    /// <summary>
    /// The pod's labels. Needed to answer the question a Service detail exists for: which pods does
    /// this selector actually reach right now (KON-167).
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Ready containers / total containers (for the "2/2" column).</summary>
    public int ReadyContainers => Containers.Count(c => c.Ready);

    /// <summary>How many init containers have finished as they should.</summary>
    public int CompletedInitContainers => InitContainers.Count(c => c.CompletedSuccessfully);

    /// <summary>Whether the pod is still working through its init containers.</summary>
    public bool IsInitialising => CompletedInitContainers < InitContainers.Count;

    /// <summary>
    /// What the status column should read. While init containers run, the phase alone says "Pending"
    /// for both a pod starting up and a pod wedged on its first init container — the difference
    /// between waiting and stuck. This reports the init phase the way <c>kubectl</c> does:
    /// <c>Init:0/2</c> while it progresses, <c>Init:CrashLoopBackOff</c> when it is not going to.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsInitialising)
                return Phase.ToString();

            var blocked = InitContainers.FirstOrDefault(c => !c.CompletedSuccessfully);

            // A reason only tells you something once it names a problem; "PodInitializing" is the
            // kubelet saying it has not started yet, which the x/y count already says better.
            if (blocked is { Reason.Length: > 0 } && blocked.Reason != "PodInitializing")
                return $"Init:{blocked.Reason}";

            if (blocked is { RunState: ContainerRunState.Terminated, ExitCode: > 0 and var code })
                return $"Init:ExitCode:{code}";

            return $"Init:{CompletedInitContainers}/{InitContainers.Count}";
        }
    }

    public TimeSpan Age { get; init; }

    /// <summary>
    /// The ConfigMaps and Secrets this pod reads, and how (KON-330) — what answers "is anything still
    /// using this?" from the other side, on a secret's own page.
    /// <para>
    /// Carried on the pod rather than asked per object, because the pod spec already says it: a listing
    /// contains every one of these fields, so filling this costs nothing and asking the other way round
    /// would be a manifest fetch per pod.
    /// </para>
    /// </summary>
    public IReadOnlyList<ConfigUse> ConfigUses { get; init; } = [];
}

/// <summary>How a pod reaches a ConfigMap or a Secret.</summary>
public enum ConfigUseKind
{
    /// <summary>Mounted as a volume — the pod sees files.</summary>
    Volume,

    /// <summary>One environment variable from one key.</summary>
    EnvironmentVariable,

    /// <summary>Every key as environment variables (<c>envFrom</c>).</summary>
    EnvironmentFrom,

    /// <summary>A registry credential the kubelet uses to pull images. Secrets only.</summary>
    ImagePullSecret,
}

/// <summary>
/// One ConfigMap or Secret a pod reads (KON-330).
/// <para>
/// The <i>how</i> is kept because it decides what deleting the object breaks and when: a volume mount
/// stops the next pod from starting at all, while a single missing environment key may only surface
/// as a confusing error deep inside the app.
/// </para>
/// </summary>
public sealed record ConfigUse
{
    /// <summary>Secret or ConfigMap.</summary>
    public required GroupVersionKind Kind { get; init; }

    public required string Name { get; init; }

    public required ConfigUseKind How { get; init; }

    /// <summary>Which container reads it; empty for volumes and pull secrets, which are pod-wide.</summary>
    public string Container { get; init; } = string.Empty;
}

/// <summary>
/// Live pod resource usage from the metrics-server — the OAL mirror of the CEAL's
/// <c>ContainerStats</c>. CPU in milli-cores, memory in bytes.
/// </summary>
public sealed record PodMetrics
{
    public required string Pod { get; init; }
    public required string Namespace { get; init; }

    /// <summary>CPU usage in milli-cores.</summary>
    public long CpuMillicores { get; init; }

    /// <summary>Memory working set in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>When the sample was taken (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }
}
