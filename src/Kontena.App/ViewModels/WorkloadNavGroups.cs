using Kontena.Core.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Which per-kind entries appear under Workloads in the sidebar (KON-169). Pure, so the rules can be
/// checked without a shell, a cluster or a rendered nav.
/// </summary>
public static class WorkloadNavGroups
{
    /// <summary>One sub-entry: a kind and how many of it exist.</summary>
    public readonly record struct Group(WorkloadKind Kind, int Count);

    /// <summary>
    /// The sub-entries for this set of workloads.
    /// <para>
    /// Only kinds that exist get one. A cluster running three Deployments and nothing else would
    /// otherwise carry four permanently-empty rows, and an empty nav item is a place the user learns
    /// not to click — the dead-button problem (KON-117) spread across the sidebar.
    /// </para>
    /// <para>
    /// Ordered by the enum rather than by count, so the sidebar does not reshuffle itself under the
    /// pointer when a Job finishes.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Group> For(IEnumerable<Workload> workloads)
    {
        var byKind = workloads
            .GroupBy(w => w.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        return
        [
            .. Enum.GetValues<WorkloadKind>()
                .Where(byKind.ContainsKey)
                .Select(kind => new Group(kind, byKind[kind])),
        ];
    }

    /// <summary>
    /// Whether the group is worth drawing at all. One kind needs no submenu: the parent already lists
    /// exactly those objects, so a single child under it is a row that says the same thing twice.
    /// </summary>
    public static bool ShouldGroup(IReadOnlyList<Group> groups) => groups.Count > 1;

    /// <summary>The nav key for a kind — "workloads:Deployment".</summary>
    public static string KeyFor(WorkloadKind kind) => "workloads:" + kind;

    /// <summary>Plural label for the sub-entry: "Deployments", "StatefulSets", …</summary>
    public static string LabelFor(WorkloadKind kind) => kind + "s";

    /// <summary>The kind a nav key addresses, or null when the key is not a per-kind workloads page.</summary>
    public static WorkloadKind? KindOf(string key) =>
        key.StartsWith("workloads:", StringComparison.Ordinal)
        && Enum.TryParse<WorkloadKind>(key["workloads:".Length..], out var kind)
            ? kind
            : null;
}
