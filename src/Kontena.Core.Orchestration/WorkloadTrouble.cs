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
