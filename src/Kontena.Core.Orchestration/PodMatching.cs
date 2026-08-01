using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration;

/// <summary>
/// Which pods belong to a workload or a service (KON-166, KON-167).
/// <para>
/// Both detail pages exist to answer that one question, so the answer lives in one place rather than
/// being written twice with two slightly different notions of "belongs to" — which is exactly how the
/// two pages would have drifted apart.
/// </para>
/// <para>
/// The two relationships are genuinely different and are kept apart on purpose. A workload owns its
/// pods, so ownership is the honest test: it cannot pick up a pod that merely happens to share labels.
/// A service has no ownership at all — it reaches whatever matches its selector at this moment, which
/// is precisely what makes "why is nothing arriving" answerable.
/// </para>
/// </summary>
public static class PodMatching
{
    /// <summary>
    /// Pods this workload controls. Matches on the owner Kontena already resolves, where a ReplicaSet
    /// is rolled up to its Deployment — so a Deployment finds its pods across revisions without
    /// listing ReplicaSets.
    /// </summary>
    public static IReadOnlyList<Pod> OwnedBy(IEnumerable<Pod> pods, Workload workload)
    {
        // A CronJob owns Jobs, and those Jobs own the pods, so no pod is ever controlled by a CronJob
        // directly. Returning an empty list is correct; presenting it as "no pods" would not be, which
        // is why the caller has OwnsPodsDirectly to say so instead.
        var owner = $"{workload.Kind}/{workload.Name}";

        return [.. pods.Where(p =>
            string.Equals(p.Namespace, workload.Namespace, StringComparison.Ordinal)
            && string.Equals(p.ControlledBy, owner, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Whether this kind owns pods directly. False only for CronJob, whose pods belong to the Jobs it
    /// creates — a distinction worth stating rather than showing an empty list that reads as "none".
    /// </summary>
    public static bool OwnsPodsDirectly(WorkloadKind kind) => kind != WorkloadKind.CronJob;

    /// <summary>Pods a service's selector reaches right now.</summary>
    public static IReadOnlyList<Pod> SelectedBy(IEnumerable<Pod> pods, Service service)
    {
        // An empty selector selects nothing here. Kubernetes treats a service with no selector as one
        // whose endpoints are managed by hand (ExternalName, or manual Endpoints), so matching every
        // pod in the namespace would be the opposite of the truth.
        if (service.Selector.Count == 0)
            return [];

        return [.. pods.Where(p =>
            string.Equals(p.Namespace, service.Namespace, StringComparison.Ordinal)
            && Matches(p.Labels, service.Selector))];
    }

    /// <summary>
    /// Whether a set of labels satisfies a selector: every selector entry must be present with the
    /// same value. Extra labels on the pod are irrelevant — that is what makes a selector a filter
    /// rather than an equality test.
    /// </summary>
    public static bool Matches(IReadOnlyDictionary<string, string> labels, IReadOnlyDictionary<string, string> selector)
    {
        foreach (var (key, value) in selector)
        {
            if (!labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
