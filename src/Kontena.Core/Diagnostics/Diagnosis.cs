namespace Kontena.Core.Diagnostics;

/// <summary>Where the suggestion at the end of a diagnosis leads.</summary>
public enum DiagnosisAction
{
    /// <summary>Nothing to click — the suggestion is advice the app cannot carry out.</summary>
    None,

    /// <summary>Open the logs of the run that ended, not the one that is starting.</summary>
    PreviousLogs,

    /// <summary>Open the live logs.</summary>
    Logs,

    /// <summary>Open the events feed for this object.</summary>
    Events,

    /// <summary>Open the manifest, where limits and probes are declared.</summary>
    Manifest,

    /// <summary>Open the inspect view, where the command and the mounts are.</summary>
    Inspect,
}

/// <summary>
/// One readable explanation of why a container or pod is not running, with the evidence it was
/// concluded from (KON-150).
/// <para>
/// Produced by deterministic rules over data the app already holds — status, events, exit codes.
/// No guessing: a rule that cannot name a cause returns nothing, because a wrong explanation costs
/// more than a missing one. Every rule matches on reason codes rather than on the engine's or
/// kubelet's wording, which is not API.
/// </para>
/// </summary>
public sealed record Diagnosis
{
    /// <summary>
    /// Stable id for the rule that fired, e.g. "CrashLoopBackOff" or "OOMKilled". Not shown to the
    /// user — it is what tests assert on, so the wording stays free to change.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>One line naming what is wrong, in the words you would use out loud.</summary>
    public required string Title { get; init; }

    /// <summary>A sentence or two explaining what that means.</summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// The facts this was read off: exit codes, counts, the kubelet's own message. Shown verbatim so
    /// the reader can check the conclusion rather than take it.
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>What to do next, when there is something worth suggesting.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Where <see cref="Suggestion"/> leads, when the page can take you there.</summary>
    public DiagnosisAction Action { get; init; } = DiagnosisAction.None;
}
