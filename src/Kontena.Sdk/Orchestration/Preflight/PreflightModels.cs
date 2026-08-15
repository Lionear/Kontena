namespace Kontena.Sdk.Orchestration.Preflight;

/// <summary>
/// What one check decided. Four values rather than a bool, because "could not be checked" is a real
/// answer and the other three do not contain it (KON-235).
/// </summary>
public enum PreflightOutcome
{
    /// <summary>Checked, and fine.</summary>
    Passed,

    /// <summary>
    /// Checked, not fine, but not a reason to stop. Reserved for things that work and will bite later —
    /// clock drift, a mix of architectures.
    /// </summary>
    Warned,

    /// <summary>Checked, and this will not work.</summary>
    Failed,

    /// <summary>
    /// The check could not be run at all — unreachable, a missing command, a timeout. Its own outcome
    /// rather than a pass or a fail: reporting "swap is off" for a machine we never reached is a lie,
    /// and reporting "swap is on" is a different lie. On a blocking check this stops the rollout, since
    /// an unverified blocker is exactly the thing that fails halfway through with a half-built cluster.
    /// </summary>
    Unknown,
}

/// <summary>
/// One named thing worth knowing before anything is installed.
/// <para>
/// Deliberately says nothing about hosts, SSH, or Kubernetes distributions. The same shape answers
/// "can this machine run a kubelet" and "may this account do what the app is about to try" (KON-72),
/// and a check list that knew about SSH could not be reused for the second.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier, e.g. <c>swap</c>. What a test or a saved result refers to.</param>
/// <param name="Title">What it is called on screen, e.g. "Swap is off".</param>
/// <param name="Blocking">
/// Whether failing it stops the run. Not a severity: it is the difference between "this cannot work"
/// and "this works and you should know something".
/// </param>
public sealed record PreflightCheck(string Id, string Title, bool Blocking);

/// <summary>
/// Something Kontena could do about a finding, where there is exactly one obvious thing to do.
/// <para>
/// Offered only where the fix is unambiguous. Turning swap off is: the kubelet refuses to start with
/// it on, and there is one command. Setting the clock is not — that is a system-wide decision about
/// time sources that belongs to whoever runs the machine, and guessing at it is how a tool starts
/// making changes nobody asked for. Same line the metrics-source install draws.
/// </para>
/// </summary>
/// <param name="Title">The button, e.g. "Turn swap off".</param>
/// <param name="Detail">What it will do, in a sentence, before it is done.</param>
/// <param name="Command">The command it will run, so it can be read first, or copied and run by hand.</param>
public sealed record PreflightRemedy(string Title, string Detail, string Command);

/// <summary>
/// One check's answer about one target, with the reason it reached it.
/// <para>
/// <see cref="Reason"/> is required by construction. "Blocked" without saying which check and why is a
/// dead end for whoever has to act on it, and a reason that is optional is a reason that goes missing
/// on the paths nobody tested.
/// </para>
/// </summary>
/// <param name="Check">Which check this is.</param>
/// <param name="Target">What it was about — a host address, a cluster name. Free-form on purpose.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Reason">Why, in a sentence someone can act on.</param>
public sealed record PreflightFinding(
    PreflightCheck Check,
    string Target,
    PreflightOutcome Outcome,
    string Reason)
{
    /// <summary>What to offer doing about it, or null when there is nothing unambiguous to offer.</summary>
    public PreflightRemedy? Remedy { get; init; }

    /// <summary>Whether this finding is what stops the run.</summary>
    public bool Blocks => Check.Blocking && Outcome is PreflightOutcome.Failed or PreflightOutcome.Unknown;

    public static PreflightFinding Pass(PreflightCheck check, string target, string reason) =>
        new(check, target, PreflightOutcome.Passed, reason);

    public static PreflightFinding Warn(PreflightCheck check, string target, string reason) =>
        new(check, target, PreflightOutcome.Warned, reason);

    public static PreflightFinding Fail(
        PreflightCheck check, string target, string reason, PreflightRemedy? remedy = null) =>
        new(check, target, PreflightOutcome.Failed, reason) { Remedy = remedy };

    /// <summary>
    /// The check did not get an answer. Takes the reason it could not, which is the only useful thing
    /// it has to say.
    /// </summary>
    public static PreflightFinding Unknown(PreflightCheck check, string target, string reason) =>
        new(check, target, PreflightOutcome.Unknown, reason);
}

/// <summary>
/// Everything one preflight run found, and the single answer the Continue button needs.
/// </summary>
public sealed record PreflightReport(IReadOnlyList<PreflightFinding> Findings)
{
    /// <summary>What stops the run: a blocking check that failed, or one that could not be run.</summary>
    public IReadOnlyList<PreflightFinding> Blockers => [.. Findings.Where(f => f.Blocks)];

    /// <summary>What does not stop it but was worth saying.</summary>
    public IReadOnlyList<PreflightFinding> Warnings =>
        [.. Findings.Where(f => f.Outcome == PreflightOutcome.Warned)];

    /// <summary>
    /// Whether the rollout may start. One value for the button, so no screen has to re-derive the rule
    /// and get it subtly different.
    /// </summary>
    public bool CanContinue => Blockers.Count == 0;

    /// <summary>Everything about one target, in the order the checks were declared.</summary>
    public IReadOnlyList<PreflightFinding> For(string target) =>
        [.. Findings.Where(f => string.Equals(f.Target, target, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// One line for the top of the page. Says what is wrong rather than only that something is — a
    /// count with no subject is the dead end this whole type exists to avoid.
    /// </summary>
    public string Summary => Blockers switch
    {
        [] when Warnings.Count == 0 => "Every machine is ready.",
        [] => $"Ready, with {Warnings.Count} {(Warnings.Count == 1 ? "warning" : "warnings")}.",
        [var one] => $"{one.Check.Title} — {one.Target}.",
        var many => $"{many.Count} checks failed, across {many.Select(b => b.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()} machines.",
    };
}
