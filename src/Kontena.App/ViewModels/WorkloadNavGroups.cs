using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// Which per-kind entries appear under Workloads in the sidebar (KON-169). Pure, so the rules can be
/// checked without a shell, a cluster or a rendered nav.
/// </summary>
public static class WorkloadNavGroups
{
    /// <summary>
    /// Which kinds this set of workloads has a sub-entry for.
    /// <para>
    /// Only kinds that exist get one. A cluster running three Deployments and nothing else would
    /// otherwise carry four permanently-empty rows, and an empty nav item is a place the user learns
    /// not to click — the dead-button problem (KON-117) spread across the sidebar.
    /// </para>
    /// <para>
    /// Ordered by the enum rather than by count, so the sidebar does not reshuffle itself under the
    /// pointer when a Job finishes. The same order <c>IClusterEngine.ListWorkloadKindsAsync</c>
    /// promises, which is where the sidebar gets its answer from now (KON-396); this overload is for
    /// the callers that are holding the objects anyway.
    /// </para>
    /// <para>
    /// Kinds rather than kinds-and-counts. The counts went off the sidebar with every other badge
    /// (KON-354), and once nothing drew them, carrying them here was the reason the cheap question
    /// could only be answered by the expensive read.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WorkloadKind> KindsIn(IEnumerable<Workload> workloads)
    {
        ArgumentNullException.ThrowIfNull(workloads);

        var present = workloads.Select(w => w.Kind).ToHashSet();

        return [.. Enum.GetValues<WorkloadKind>().Where(present.Contains)];
    }

    /// <summary>
    /// Whether the group is worth drawing at all. One kind needs no submenu: the parent already lists
    /// exactly those objects, so a single child under it is a row that says the same thing twice.
    /// </summary>
    public static bool ShouldGroup(IReadOnlyList<WorkloadKind> kinds) => kinds.Count > 1;

    /// <summary>The nav key for a kind — "workloads:Deployment".</summary>
    public static string KeyFor(WorkloadKind kind) => "workloads:" + kind;

    /// <summary>Plural label for the sub-entry: "Deployments", "StatefulSets", …</summary>
    public static string LabelFor(WorkloadKind kind) => kind + "s";

    /// <summary>
    /// The page key that still means something here (KON-200). A per-kind page survives a namespace
    /// switch as a key even when the kind does not survive it as an object: standing on
    /// <c>workloads:DaemonSet</c> and moving to a namespace with no DaemonSets would otherwise open an
    /// empty list under a sidebar entry that is about to be removed. Falling back to Workloads is the
    /// nearest page that is still about something.
    /// </summary>
    public static string ResolveKey(string key, IReadOnlyList<WorkloadKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        return KindOf(key) is { } kind && !kinds.Contains(kind) ? "workloads" : key;
    }

    /// <summary>The kind a nav key addresses, or null when the key is not a per-kind workloads page.</summary>
    public static WorkloadKind? KindOf(string key) =>
        key.StartsWith("workloads:", StringComparison.Ordinal)
        && Enum.TryParse<WorkloadKind>(key["workloads:".Length..], out var kind)
            ? kind
            : null;
}
