using Kontena.Core.Diagnostics;
using Kontena.Core.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Why this pod is not running, read off the status and the events the app already fetched
/// (KON-150).
/// <para>
/// The five cases below are the ones that account for nearly every stuck pod, and each of them is
/// spread over four tabs today: the reason code is on the pod, the detail is in an event, the exit
/// code is on the container status, and the limit is in the manifest. This joins them into one
/// explanation and stops. Anything it does not recognise returns <c>null</c> — <see cref="Diagnose"/>
/// never speculates, because a confident wrong answer sends someone looking in the wrong place.
/// </para>
/// </summary>
public static class PodDiagnosis
{
    /// <summary>
    /// Explain what is wrong with <paramref name="pod"/>, or <c>null</c> when nothing recognisable is.
    /// <paramref name="events"/> may be empty: every rule states what it can from the status alone
    /// and treats an event as extra evidence, never as a precondition.
    /// </summary>
    public static Diagnosis? Diagnose(Pod pod, IReadOnlyList<ClusterEvent>? events = null)
    {
        var own = Own(pod, events);

        return ImagePull(pod, own)
               ?? ConfigError(pod, own)
               ?? CrashLoop(pod, own)
               ?? OutOfMemory(pod)
               ?? Unschedulable(pod, own)
               ?? ProbeFailing(pod, own);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    /// <summary>The image cannot be pulled — which is either "you may not" or "it is not there".</summary>
    private static Diagnosis? ImagePull(Pod pod, IReadOnlyList<ClusterEvent> events)
    {
        var blocked = Waiting(pod, "ImagePullBackOff", "ErrImagePull", "InvalidImageName");
        if (blocked is null)
            return null;

        var failure = events.FirstOrDefault(e => e.Reason is "Failed" or "FailedToPull");
        var message = failure?.Message ?? string.Empty;

        // Match the registry's answer, not its phrasing where we can help it: these substrings are
        // what a registry returns over the wire, not what any one kubelet build calls it.
        var cause =
            Mentions(message, "unauthorized", "authentication required", "pull access denied", "denied:")
                ? "The registry refused the credentials — this pod either has no imagePullSecret or the one it has cannot read that repository."
            : Mentions(message, "not found", "manifest unknown", "404")
                ? "The registry answered, but the repository or tag does not exist there."
            : Mentions(message, "no such host", "timeout", "connection refused", "i/o timeout")
                ? "The registry could not be reached from the node."
                : "The kubelet could not pull the image.";

        var evidence = new List<string> { $"Image: {blocked.Image}", $"Registry: {RegistryOf(blocked.Image)}" };
        AddMessage(evidence, failure);

        return new Diagnosis
        {
            Code = "ImagePullBackOff",
            Title = $"The image for \"{blocked.Name}\" cannot be pulled",
            Explanation = cause,
            Evidence = evidence,
            Suggestion = "The events carry the registry's own answer in full.",
            Action = DiagnosisAction.Events,
        };
    }

    /// <summary>A ConfigMap or Secret the pod mounts is not there — the kubelet says which.</summary>
    private static Diagnosis? ConfigError(Pod pod, IReadOnlyList<ClusterEvent> events)
    {
        var blocked = Waiting(pod, "CreateContainerConfigError", "CreateContainerError");
        if (blocked is null)
            return null;

        var failure = events.FirstOrDefault(e => e.Reason is "Failed");
        var evidence = new List<string>();
        AddMessage(evidence, failure);

        return new Diagnosis
        {
            Code = blocked.Reason,
            Title = $"Container \"{blocked.Name}\" cannot be created",
            Explanation = blocked.Reason == "CreateContainerConfigError"
                ? "Something the container's configuration refers to is missing — usually a ConfigMap, a Secret or a key inside one. The pod is valid; what it points at is not there."
                : "The kubelet accepted the pod but could not create the container.",
            Evidence = evidence,
            Suggestion = "The manifest shows what this container mounts and reads.",
            Action = DiagnosisAction.Manifest,
        };
    }

    /// <summary>It starts, it exits, it waits, it starts again.</summary>
    private static Diagnosis? CrashLoop(Pod pod, IReadOnlyList<ClusterEvent> events)
    {
        var blocked = Waiting(pod, "CrashLoopBackOff");
        if (blocked is null)
            return null;

        var evidence = new List<string> { $"Restarts: {blocked.Restarts}" };

        if (blocked.LastExitCode is { } code)
            evidence.Add($"Last exit code: {code}{SignalSuffix(code)}");

        if (blocked.LastTerminationReason.Length > 0)
            evidence.Add($"Last termination reason: {blocked.LastTerminationReason}");

        var backoff = events.FirstOrDefault(e => e.Reason == "BackOff");
        if (backoff is not null)
            evidence.Add($"Last restart: {Ago(backoff.LastSeen)}");

        var oom = IsOom(blocked.LastTerminationReason, blocked.LastExitCode);

        return new Diagnosis
        {
            Code = "CrashLoopBackOff",
            Title = blocked.Kind == ContainerKind.Init
                ? $"Init container \"{blocked.Name}\" keeps failing"
                : $"Container \"{blocked.Name}\" keeps crashing",
            Explanation = oom
                ? OomSentence(blocked) + " Because it restarts, the kubelet backs off further each time."
                : blocked.Kind == ContainerKind.Init
                    ? "It exits, the kubelet waits and starts it again. The app containers do not start until this one succeeds, so the pod stays where it is."
                    : "It starts, exits and is restarted, with the kubelet waiting longer between attempts each time. Why it exits is in the logs of the run that ended — not in the one starting now.",
            Evidence = evidence,
            Suggestion = "Read the logs of the previous run — the live logs belong to the attempt that has not failed yet.",
            Action = DiagnosisAction.PreviousLogs,
        };
    }

    /// <summary>Killed for using more memory than it was allowed.</summary>
    private static Diagnosis? OutOfMemory(Pod pod)
    {
        var killed = pod.AllContainers.FirstOrDefault(c =>
            (c.RunState == ContainerRunState.Terminated && IsOom(c.Reason, c.ExitCode))
            || IsOom(c.LastTerminationReason, c.LastExitCode));

        if (killed is null)
            return null;

        var evidence = new List<string>
        {
            $"Exit code: {killed.ExitCode ?? killed.LastExitCode ?? 137} (SIGKILL)",
            killed.MemoryLimitBytes is { } limit
                ? $"Memory limit: {ByteSize.Format(limit)}"
                : "Memory limit: none declared",
        };

        return new Diagnosis
        {
            Code = "OOMKilled",
            Title = $"Container \"{killed.Name}\" was killed for using too much memory",
            Explanation = OomSentence(killed),
            Evidence = evidence,
            Suggestion = killed.MemoryLimitBytes is null
                ? "Give the container a memory limit, or find out what is holding the memory."
                : "Raise the limit if the container needs it, or find out what is holding the memory.",
            Action = DiagnosisAction.Manifest,
        };
    }

    /// <summary>The scheduler cannot place it, and its message says exactly why.</summary>
    private static Diagnosis? Unschedulable(Pod pod, IReadOnlyList<ClusterEvent> events)
    {
        if (pod.Phase != PodPhase.Pending || pod.Node.Length > 0)
            return null;

        var failed = events.FirstOrDefault(e => e.Reason == "FailedScheduling");

        // Pending with no node and nothing said about it is a pod that was created a second ago.
        // Calling that a fault is the mistake this rule exists to avoid.
        if (failed is null)
            return null;

        var evidence = new List<string>();
        AddMessage(evidence, failed);

        return new Diagnosis
        {
            Code = "FailedScheduling",
            Title = "No node can run this pod",
            Explanation = "The scheduler looked at every node and rejected all of them. Its message below names the reason per node — most often too little CPU or memory left, a taint the pod does not tolerate, or a volume that only exists on other nodes.",
            Evidence = evidence,
            Suggestion = "The pod's requests, tolerations and volumes are in the manifest.",
            Action = DiagnosisAction.Manifest,
        };
    }

    /// <summary>Running, but the kubelet's probe says it is not usable.</summary>
    private static Diagnosis? ProbeFailing(Pod pod, IReadOnlyList<ClusterEvent> events)
    {
        var unhealthy = events.FirstOrDefault(e => e.Reason == "Unhealthy");
        if (unhealthy is null)
            return null;

        // A single failed probe on a pod that went ready anyway is history, not a diagnosis.
        var notReady = pod.Containers.FirstOrDefault(c => c.RunState == ContainerRunState.Running && !c.Ready);
        if (notReady is null)
            return null;

        // "Liveness probe failed: …" / "Readiness probe failed: …" — the kubelet leads with which one,
        // and that is the one thing the event says that the status does not.
        var kind = unhealthy.Message.StartsWith("Liveness", StringComparison.OrdinalIgnoreCase) ? "liveness"
            : unhealthy.Message.StartsWith("Startup", StringComparison.OrdinalIgnoreCase) ? "startup"
            : "readiness";

        var evidence = new List<string>();
        AddMessage(evidence, unhealthy);

        return new Diagnosis
        {
            Code = "Unhealthy",
            Title = $"The {kind} probe for \"{notReady.Name}\" is failing",
            Explanation = kind == "readiness"
                ? "The container is running, but until its readiness probe passes no Service will send traffic to it. The probe's own answer is below."
                : $"The container is running, but its {kind} probe does not answer as expected — a failing liveness probe has the kubelet restart the container.",
            Evidence = evidence,
            Suggestion = "The probe's path, port and timings are in the manifest.",
            Action = DiagnosisAction.Manifest,
        };
    }

    // ── Shared reading ───────────────────────────────────────────────────────

    /// <summary>
    /// The first container held up by one of <paramref name="reasons"/>, init containers first: a pod
    /// wedged on its init container reports the same reason on nothing else, and it is the one whose
    /// logs you want.
    /// </summary>
    private static ContainerStatus? Waiting(Pod pod, params string[] reasons) =>
        pod.AllContainers.FirstOrDefault(c =>
            c.RunState == ContainerRunState.Waiting && reasons.Contains(c.Reason, StringComparer.Ordinal));

    /// <summary>Events about this pod, newest first — a caller may hand us a whole namespace.</summary>
    private static IReadOnlyList<ClusterEvent> Own(Pod pod, IReadOnlyList<ClusterEvent>? events) =>
        events is null
            ? []
            : [.. events
                .Where(e => string.Equals(e.InvolvedObject.Name, pod.Name, StringComparison.Ordinal))
                .OrderByDescending(e => e.LastSeen)];

    private static void AddMessage(List<string> evidence, ClusterEvent? e)
    {
        if (e is { Message.Length: > 0 })
            evidence.Add(e.Count > 1 ? $"{e.Message} (×{e.Count})" : e.Message);
    }

    /// <summary>
    /// An OOM kill is what the reason says it is. Exit code 137 on its own is 128+SIGKILL and any
    /// <c>kill -9</c> produces it, so the code only counts as corroboration once the reason agrees.
    /// </summary>
    private static bool IsOom(string reason, int? exitCode) =>
        string.Equals(reason, "OOMKilled", StringComparison.Ordinal)
        || (exitCode == 137 && reason.Length == 0);

    /// <summary>
    /// Whether the container itself took the SIGKILL decides what its logs are worth. 137 is the
    /// container's own process being killed outright; the same reason with another exit code means
    /// something inside it was killed and the process then exited by itself — and then it did get to
    /// write something.
    /// </summary>
    private static string OomSentence(ContainerStatus c)
    {
        var ceiling = c.MemoryLimitBytes is { } limit
            ? $"its memory limit of {ByteSize.Format(limit)}"
            : "the memory available to it (no limit is declared, so the ceiling is the node's)";

        return (c.ExitCode ?? c.LastExitCode) == 137
            ? $"It asked for more memory than {ceiling} and the kernel killed it. The container did not choose to exit — nothing it logged will say why it stopped."
            : $"A process inside it went over {ceiling} and the kernel killed that process; the container then exited by itself. What it wrote before stopping is worth reading.";
    }

    private static string SignalSuffix(int code) => code switch
    {
        137 => " (SIGKILL)",
        143 => " (SIGTERM)",
        139 => " (SIGSEGV)",
        _ => "",
    };

    private static bool Mentions(string message, params string[] needles) =>
        needles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The registry an image reference points at. Docker Hub is the implicit default and the only one
    /// that is not written down, so a reference with no host is not "unknown" — it is Docker Hub.
    /// </summary>
    private static string RegistryOf(string image)
    {
        var slash = image.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return "docker.io";

        var head = image[..slash];
        return head.Contains('.', StringComparison.Ordinal)
               || head.Contains(':', StringComparison.Ordinal)
               || head == "localhost"
            ? head
            : "docker.io";
    }

    private static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.UtcNow - moment;
        return span < TimeSpan.Zero ? "just now"
            : span.TotalMinutes < 1 ? "less than a minute ago"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }
}
