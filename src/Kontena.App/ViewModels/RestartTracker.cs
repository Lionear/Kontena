using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Remembers which workloads were asked to restart, until the cluster has actually shown the restart
/// happening (KON-448).
/// <para>
/// The problem this exists for is a race, not a delay. <c>RolloutRestartAsync</c> is a PATCH that
/// returns before Kubernetes has moved a single pod, so the reload that follows it can easily read
/// the workload back as <see cref="RolloutStatus.Complete"/> — the same green "Complete" it showed a
/// second earlier. Padding the reload with a sleep would only make the app slower at being wrong.
/// Instead the row says "Restarting…" from the click until a <see cref="RolloutStatus.Progressing"/>
/// reading proves the rollout is under way, and from there the real status takes over.
/// </para>
/// <para>
/// Owned by <see cref="MainWindowViewModel"/> because that is where the restart is invoked from and
/// because it outlives the rows and detail pages, which are rebuilt on every visit — an entry held
/// by either of those would be thrown away by the reload the restart itself triggers.
/// </para>
/// </summary>
public sealed class RestartTracker
{
    /// <summary>What the status reads while a requested restart has not shown up yet.</summary>
    public const string Restarting = "Restarting…";

    /// <summary>
    /// Amber, the same as <see cref="RolloutStatus.Progressing"/> — because that is what this is.
    /// KON-420 settled the status language and put a plain restart in the amber tier on purpose; a
    /// fifth colour for the seconds before the cluster confirms it would be a new word for a state
    /// the language already has a word for. The label carries the difference.
    /// </summary>
    public const string RestartingColour = "#F5B14C";

    /// <summary>
    /// How long an unproven request is believed. Not a timing effect: nothing waits for this, and a
    /// rollout that reports Progressing after two minutes still gets its real pill. It only stops a
    /// watch that dropped — or a restart the apiserver quietly ignored — from leaving a row saying
    /// "Restarting…" for the rest of the session.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly Dictionary<ResourceRef, Pending> _pending = [];
    private readonly Action<string>? _onRestarted;

    /// <param name="onRestarted">Told the message when a tracked rollout is seen finishing. Null in
    /// tests, which are interested in the state machine and not in the toast.</param>
    public RestartTracker(Action<string>? onRestarted = null) => _onRestarted = onRestarted;

    private sealed class Pending
    {
        public DateTimeOffset Requested { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        /// <summary>Whether the cluster has confirmed the rollout is moving. Until it has, a
        /// "Complete" reading is more likely to be the old one than a finished restart.</summary>
        public bool Moving { get; set; }
    }

    /// <summary>A restart has been asked for. Call once the PATCH has come back without throwing.</summary>
    public void Requested(Workload workload, DateTimeOffset now) =>
        _pending[workload.Reference] = new Pending
        {
            Requested = now,
            Kind = workload.Kind.ToString(),
            Name = workload.Name,
        };

    /// <summary>
    /// Take in a fresh reading of the cluster — a list load, a watch update, a detail refresh. Every
    /// caller passes everything it just read; workloads nobody asked to restart cost a dictionary
    /// miss each.
    /// </summary>
    public void Observe(IEnumerable<Workload> workloads, DateTimeOffset now)
    {
        foreach (var w in workloads)
        {
            if (!_pending.TryGetValue(w.Reference, out var pending))
                continue;

            if (w.RolloutStatus == RolloutStatus.Progressing)
            {
                pending.Moving = true;
            }
            else if (w.RolloutStatus == RolloutStatus.Complete && pending.Moving)
            {
                // Progressing → Complete is the only transition that means the restart finished. A
                // Complete without a Progressing before it is the reading that has not caught up.
                _pending.Remove(w.Reference);
                _onRestarted?.Invoke($"{pending.Kind} \"{pending.Name}\" restarted");
            }
            else if (now - pending.Requested > Patience)
            {
                _pending.Remove(w.Reference);
            }
        }
    }

    /// <summary>
    /// Whether this workload should read "Restarting…" instead of whatever status came back. True
    /// only between the click and the first Progressing reading — once the rollout is visibly under
    /// way the cluster's own answer is the better one, and it says the same thing in amber.
    /// </summary>
    public bool IsRestarting(ResourceRef reference, DateTimeOffset now)
    {
        if (!_pending.TryGetValue(reference, out var pending))
            return false;

        if (pending.Moving)
            return false;

        // Expiry is checked here as well as in Observe: a page the user navigated away from stops
        // observing, and an entry that only ever expired on the next reading would come back to a
        // row that has been sitting on a stale request for an hour.
        if (now - pending.Requested <= Patience)
            return true;

        _pending.Remove(reference);
        return false;
    }
}
