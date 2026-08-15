using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>How far one machine has got. In the order they happen, so they compare.</summary>
public enum RolloutStep
{
    /// <summary>Reaching the machine at all.</summary>
    Connect,

    /// <summary>Getting the k0s binary onto it.</summary>
    Binary,

    /// <summary>Joining it to the cluster.</summary>
    Join,

    /// <summary>Starting the service that keeps it there.</summary>
    Service,

    /// <summary>The node reporting itself ready.</summary>
    Healthy,
}

/// <summary>What one machine is doing.</summary>
public enum RolloutHostState
{
    /// <summary>Not started. The rollout does controllers first, so workers wait.</summary>
    Waiting,

    /// <summary>Being worked on right now.</summary>
    Running,

    /// <summary>Installed and up.</summary>
    Done,

    /// <summary>Stopped on this machine. What it is left holding is in <see cref="RolloutHost.Detail"/>.</summary>
    Failed,

    /// <summary>Deliberately left out of the cluster after a failure — "continue without this one".</summary>
    Skipped,
}

/// <summary>One machine's row on the progress screen.</summary>
public sealed class RolloutHost(string address, ClusterHostRole role)
{
    public string Address { get; } = address;

    public ClusterHostRole Role { get; } = role;

    public RolloutStep Step { get; internal set; } = RolloutStep.Connect;

    public RolloutHostState State { get; internal set; } = RolloutHostState.Waiting;

    /// <summary>The line that explains the current state — the tool's words, not a paraphrase.</summary>
    public string? Detail { get; internal set; }

    /// <summary>
    /// Every line of output that named this machine. What "diagnose this host" shows: the tool already
    /// said what went wrong, and the job is to find it in the wall rather than to reword it.
    /// </summary>
    public List<string> Lines { get; } = [];

    public bool IsFinished => State is RolloutHostState.Done or RolloutHostState.Skipped;
}

/// <summary>
/// Turns a stream of k0sctl output into a row per machine (KON-239).
/// <para>
/// An <b>overlay</b>, never a replacement. The console shows the tool's own lines verbatim — the same
/// choice the local create screen made — and this reads along to say which machine is where. When the
/// reading is wrong the console is still right, which is the only reason a heuristic is acceptable here
/// at all.
/// </para>
/// </summary>
public sealed class RolloutTracker
{
    // ponytail: substring matching against the host address and a handful of step words. k0sctl's
    // output shape is not a contract and has changed between minor versions, so anything stricter
    // would be a parser that breaks silently on upgrade. If k0sctl grows machine-readable output
    // (--json or similar), swap Consume for that and delete the word lists.
    private static readonly (RolloutStep Step, string[] Words)[] Vocabulary =
    [
        (RolloutStep.Healthy, ["is ready", "became ready", "node ready", "healthy"]),
        (RolloutStep.Service, ["starting service", "service started", "starting k0s", "enable"]),
        (RolloutStep.Join, ["joining", "join", "installing k0s", "install k0s"]),
        (RolloutStep.Binary, ["uploading", "downloading", "binary", "k0s binary", "upload"]),
        (RolloutStep.Connect, ["connect", "connected", "is running", "detecting"]),
    ];

    private static readonly string[] Trouble = ["error", "failed", "failure", "cannot", "unable to"];

    private readonly List<RolloutHost> _hosts;

    public RolloutTracker(IReadOnlyList<RemoteClusterHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        _hosts = [.. hosts.Select(h => new RolloutHost(h.Address, h.Role))];
    }

    public IReadOnlyList<RolloutHost> Hosts => _hosts;

    /// <summary>Machines that finished. What a resumed run does not need to do again.</summary>
    public IReadOnlyList<RolloutHost> Standing =>
        [.. _hosts.Where(h => h.State == RolloutHostState.Done)];

    /// <summary>The one that stopped, or null. There is at most one: k0sctl stops at the first.</summary>
    public RolloutHost? Stopped => _hosts.FirstOrDefault(h => h.State == RolloutHostState.Failed);

    /// <summary>Reads one line of the tool's output and moves whatever it is about.</summary>
    public void Consume(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (Match(line) is not { } host)
            return;

        host.Lines.Add(line);

        // Trouble first: a line can name a step and a failure at once, and the failure is the news.
        if (Trouble.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            host.State = RolloutHostState.Failed;
            host.Detail = line.Trim();
            return;
        }

        if (Step(line) is not { } step)
            return;

        // Never backwards. Later output can mention an earlier word — "connected" turns up again in a
        // summary — and a row that walks back reads as the machine having come undone.
        if (step >= host.Step)
        {
            host.Step = step;
            host.Detail = line.Trim();
        }

        host.State = step == RolloutStep.Healthy ? RolloutHostState.Done : RolloutHostState.Running;
    }

    /// <summary>
    /// The run ended cleanly: everything still running is up. k0sctl exits zero only when the cluster
    /// is, so this is the tool's verdict rather than ours — the alternative is a screen that says
    /// "installing" forever because the last line did not use a word we know.
    /// </summary>
    public void Finish()
    {
        foreach (var host in _hosts.Where(h => h.State is RolloutHostState.Running or RolloutHostState.Waiting))
        {
            host.Step = RolloutStep.Healthy;
            host.State = RolloutHostState.Done;
        }
    }

    /// <summary>
    /// The run stopped. Whatever was mid-flight is what failed; anything untouched stays waiting,
    /// because it genuinely was not reached and saying otherwise would invent a fact.
    /// </summary>
    public void Fail(string? complaint)
    {
        if (_hosts.Any(h => h.State == RolloutHostState.Failed))
            return;

        var running = _hosts.FirstOrDefault(h => h.State == RolloutHostState.Running);
        if (running is null)
            return;

        running.State = RolloutHostState.Failed;
        running.Detail = string.IsNullOrWhiteSpace(complaint) ? running.Detail : complaint.Trim();
    }

    /// <summary>Leaves a machine out of the cluster — "continue without this one".</summary>
    public void Skip(string address)
    {
        if (_hosts.FirstOrDefault(h => Same(h.Address, address)) is { } host)
            host.State = RolloutHostState.Skipped;
    }

    /// <summary>
    /// Puts back what a previous run got done, so a resumed rollout does not start from zero on screen.
    /// Only ever marks machines <i>done</i>: a record of a failure is history, and this run has not
    /// failed yet.
    /// </summary>
    public void Restore(IEnumerable<string> standing)
    {
        ArgumentNullException.ThrowIfNull(standing);

        foreach (var address in standing)
        {
            if (_hosts.FirstOrDefault(h => Same(h.Address, address)) is not { } host)
                continue;

            host.Step = RolloutStep.Healthy;
            host.State = RolloutHostState.Done;
            host.Detail = "Already installed by an earlier run.";
        }
    }

    private RolloutHost? Match(string line) =>
        _hosts.FirstOrDefault(h => line.Contains(h.Address, StringComparison.OrdinalIgnoreCase));

    private static RolloutStep? Step(string line)
    {
        foreach (var (step, words) in Vocabulary)
        {
            if (words.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return step;
        }

        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
