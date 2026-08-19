using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Why a workload is not running as intended, in the words you would use to explain it (KON-174).
/// <para>
/// The rollout status alone says <c>Degraded</c>, which is the fact you already had from the list.
/// The reason lives one level down, in the pods — so this reads them through the seam KON-166 built
/// and the container detail KON-168 added, rather than restating the status in longer words.
/// </para>
/// </summary>
public static class WorkloadTrouble
{
    /// <summary>Whether this workload belongs on the attention list at all.</summary>
    public static bool NeedsAttention(Workload workload) =>
        workload.RolloutStatus == RolloutStatus.Degraded;

    /// <summary>
    /// One sentence naming what is wrong. Prefers what a pod is actually doing over what the
    /// controller concluded — "Pod in CrashLoopBackOff" tells you where to look; "Degraded" does not.
    /// </summary>
    public static string Describe(Workload workload, IReadOnlyList<Pod> ownedPods)
    {
        // An init container that will not finish is the most specific thing we can say, and the least
        // obvious from anywhere else: the pod reads as Pending and its app containers never started.
        foreach (var pod in ownedPods)
        {
            var stuckInit = pod.InitContainers.FirstOrDefault(c =>
                c.RunState == ContainerRunState.Waiting && Looping(c.Reason));

            if (stuckInit is not null)
                return $"Init container {stuckInit.Name} is not completing — {Humanise(stuckInit.Reason)}";
        }

        foreach (var pod in ownedPods)
        {
            var failing = pod.Containers.FirstOrDefault(c =>
                c.RunState == ContainerRunState.Waiting && c.Reason.Length > 0);

            if (failing is not null)
                return ownedPods.Count == 1
                    ? $"Pod in {failing.Reason}"
                    : $"{Count(ownedPods, failing.Reason)} of {ownedPods.Count} pods in {failing.Reason}";
        }

        // Nothing is wrong with the pods that exist, so the trouble is that they do not exist: a
        // scheduler that cannot place them, a quota, an image that will not pull into any pod at all.
        if (ownedPods.Count == 0)
            return workload.Desired > 0
                ? $"No pods running — {workload.Desired} wanted"
                : "No pods running";

        var notReady = ownedPods.Count(p => p.ReadyContainers < p.Containers.Count);
        return notReady > 0
            ? $"{notReady} of {ownedPods.Count} pods not ready"
            : "Not at its desired count";
    }

    /// <summary>
    /// The same question asked of one pod: what is wrong with it, or <c>null</c> when nothing is
    /// (KON-415). A pods list only says "Running" for a pod whose container is in CrashLoopBackOff,
    /// because the phase is about the pod and the trouble is in a container one level down.
    /// <para>
    /// The words are <see cref="Describe"/>'s on purpose — the reason stays leading, so a row reads
    /// "Pod in CrashLoopBackOff" and says where to look, rather than "Degraded" and where not to.
    /// </para>
    /// </summary>
    public static string? DescribePod(Pod pod)
    {
        // Same order as Describe: the init container is the most specific thing we can say, and the
        // one the phase hides hardest — the pod reads as Pending and its app containers never started.
        var stuckInit = pod.InitContainers.FirstOrDefault(c =>
            c.RunState == ContainerRunState.Waiting && Looping(c.Reason));

        // Shorter than Describe's sentence on purpose: this one has to fit a table cell, and the
        // container name plus the reason is the whole of what it has to point at. The sentence with
        // the explanation in it is the detail page's job, where PodDiagnosis already tells it.
        if (stuckInit is not null)
            return $"Init container {stuckInit.Name} in {stuckInit.Reason}";

        var failing = pod.Containers.FirstOrDefault(c =>
            c.RunState == ContainerRunState.Waiting && Looping(c.Reason));

        if (failing is not null)
            return $"Pod in {failing.Reason}";

        if (pod.Phase == PodPhase.Failed)
            return "Pod failed";

        // No restart timestamps reach us, so "keeps restarting" can only be read off the count: a pod
        // that has restarted this often is not one that had a bad afternoon once.
        // ponytail: fixed threshold, swap for a rate once the SDK carries the last restart time.
        if (pod.Restarts >= RestartsThatLookLikeALoop)
            return $"Restarted {pod.Restarts} times";

        // Only once it is Running. A pod still pulling or working through its init containers has
        // no ready containers either, and calling that trouble would mark every pod that starts.
        var notReady = pod.Containers.Count - pod.ReadyContainers;
        if (pod.Phase == PodPhase.Running && notReady > 0)
            return $"{notReady} of {pod.Containers.Count} containers not ready";

        return null;
    }

    /// <summary>Restart count at which a pod stops looking unlucky and starts looking stuck.</summary>
    private const int RestartsThatLookLikeALoop = 5;

    /// <summary>Waiting reasons that mean "it keeps trying and keeps failing".</summary>
    private static bool Looping(string reason) =>
        reason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "CreateContainerError";

    private static string Humanise(string reason) => reason switch
    {
        "CrashLoopBackOff" => "it exits and restarts",
        "ImagePullBackOff" or "ErrImagePull" => "its image cannot be pulled",
        "CreateContainerError" => "the container cannot be created",
        _ => reason,
    };

    private static int Count(IReadOnlyList<Pod> pods, string reason) =>
        pods.Count(p => p.Containers.Any(c => c.Reason == reason));
}
