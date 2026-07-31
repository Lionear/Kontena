namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// What a drain is allowed to do (KON-251). Every option here exists because the answer is a
/// judgement the caller has to make, not one an adapter can make for them.
/// </summary>
public sealed record DrainOptions
{
    /// <summary>
    /// Evict pods that hold an <c>emptyDir</c> volume, whose contents are lost when the pod goes.
    /// <para>
    /// Off by default, and asked for separately from the drain itself: this is the only option here
    /// that destroys something, and folding it into a general "are you sure" would be a dialog that
    /// understates what it does.
    /// </para>
    /// </summary>
    public bool DeleteEmptyDirData { get; init; }

    /// <summary>
    /// Leave DaemonSet-managed pods where they are, which is the only workable answer: their
    /// controller puts them straight back, so evicting them is a loop rather than a drain.
    /// </summary>
    public bool IgnoreDaemonSets { get; init; } = true;

    /// <summary>How long to wait for a pod to actually go before saying it has not.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>What happened to one pod — or to the node — during a drain.</summary>
public enum DrainAction
{
    /// <summary>The node was marked unschedulable. Always the first step.</summary>
    Cordoned,

    /// <summary>Left alone, with a reason: a DaemonSet pod, a mirror pod, one already finished.</summary>
    Skipped,

    /// <summary>Eviction requested; the pod is going.</summary>
    Evicting,

    /// <summary>The pod is gone.</summary>
    Evicted,

    /// <summary>
    /// A PodDisruptionBudget will not allow it right now. A real answer about the cluster's own
    /// rules, not a failure of the drain.
    /// </summary>
    Blocked,

    /// <summary>The eviction was refused or never completed.</summary>
    Failed,

    /// <summary>Every pod has been dealt with; the drain is over.</summary>
    Finished,
}

/// <summary>One step of a drain, as it happens.</summary>
public sealed record DrainProgress
{
    public required DrainAction Action { get; init; }

    /// <summary>The pod this step is about; empty for the cordon and finish steps.</summary>
    public string Pod { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Why, in the words of whatever decided it. For <see cref="DrainAction.Blocked"/> that is the
    /// apiserver's own message, which names the budget — "drain failed" would send someone to the
    /// terminal to find out which one.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
