using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Xunit;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// Which pods belong to a workload or a service (KON-166, KON-167). Both detail pages exist to answer
/// that, and both tickets warn that answering it in two places is how the pages end up disagreeing —
/// so the rules are pinned here once.
/// </summary>
public sealed class PodMatchingTests
{
    private static Pod Pod(
        string name, string ns = "app", string owner = "", IReadOnlyDictionary<string, string>? labels = null) => new()
    {
        Name = name,
        Namespace = ns,
        ControlledBy = owner,
        Labels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            map[key] = value;
        return map;
    }

    private static Workload Workload(
        string name, WorkloadKind kind = WorkloadKind.Deployment, string ns = "app") => new()
    {
        Name = name,
        Namespace = ns,
        Kind = kind,
    };

    private static Service Service(string name, IReadOnlyDictionary<string, string> selector, string ns = "app") => new()
    {
        Name = name,
        Namespace = ns,
        Selector = selector,
    };

    // ── Workload → pods, by ownership ─────────────────────────────────────────

    [Fact]
    public void A_workload_finds_the_pods_it_controls()
    {
        var pods = new[]
        {
            Pod("api-1", owner: "Deployment/api"),
            Pod("api-2", owner: "Deployment/api"),
            Pod("web-1", owner: "Deployment/web"),
        };

        Assert.Equal(["api-1", "api-2"], PodMatching.OwnedBy(pods, Workload("api")).Select(p => p.Name));
    }

    [Fact]
    public void Ownership_is_used_rather_than_labels_so_a_shared_label_cannot_leak_pods_in()
    {
        // Two workloads under one app label is ordinary — a Deployment and the Job that migrates for
        // it, say. Matching on labels here would show each the other's pods.
        var pods = new[]
        {
            Pod("api-1", owner: "Deployment/api", labels: Labels(("app", "api"))),
            Pod("migrate-1", owner: "Job/migrate", labels: Labels(("app", "api"))),
        };

        Assert.Equal(["api-1"], PodMatching.OwnedBy(pods, Workload("api")).Select(p => p.Name));
    }

    [Fact]
    public void A_workload_does_not_reach_across_namespaces()
    {
        var pods = new[]
        {
            Pod("api-1", ns: "app", owner: "Deployment/api"),
            Pod("api-1", ns: "staging", owner: "Deployment/api"),
        };

        Assert.Single(PodMatching.OwnedBy(pods, Workload("api", ns: "app")));
    }

    [Fact]
    public void A_pod_owned_by_a_different_kind_of_the_same_name_is_not_matched()
    {
        // A Deployment and a StatefulSet can share a name; the owner string carries both halves for
        // exactly this reason.
        var pods = new[] { Pod("db-0", owner: "StatefulSet/db") };

        Assert.Empty(PodMatching.OwnedBy(pods, Workload("db", WorkloadKind.Deployment)));
    }

    [Fact]
    public void A_CronJob_is_known_not_to_own_pods_directly()
    {
        // Its Jobs do. The detail page needs to say that rather than render an empty list, which would
        // describe a perfectly healthy CronJob as though something were wrong.
        Assert.False(PodMatching.OwnsPodsDirectly(WorkloadKind.CronJob));
        Assert.True(PodMatching.OwnsPodsDirectly(WorkloadKind.Deployment));
        Assert.True(PodMatching.OwnsPodsDirectly(WorkloadKind.Job));
    }

    // ── Service → pods, by selector ───────────────────────────────────────────

    [Fact]
    public void A_service_reaches_the_pods_its_selector_matches()
    {
        var pods = new[]
        {
            Pod("api-1", labels: Labels(("app", "api"))),
            Pod("web-1", labels: Labels(("app", "web"))),
        };

        Assert.Equal(["api-1"], PodMatching.SelectedBy(pods, Service("api", Labels(("app", "api")))).Select(p => p.Name));
    }

    [Fact]
    public void Extra_labels_on_a_pod_do_not_stop_it_matching()
    {
        // A selector filters; it is not an equality test. Pods routinely carry pod-template-hash and
        // whatever else the tooling adds.
        var pods = new[] { Pod("api-1", labels: Labels(("app", "api"), ("pod-template-hash", "7d9c"))) };

        Assert.Single(PodMatching.SelectedBy(pods, Service("api", Labels(("app", "api")))));
    }

    [Fact]
    public void Every_selector_entry_has_to_match()
    {
        var pods = new[] { Pod("api-1", labels: Labels(("app", "api"))) };

        Assert.Empty(PodMatching.SelectedBy(pods, Service("api", Labels(("app", "api"), ("tier", "backend")))));
    }

    [Fact]
    public void A_selector_with_no_entries_reaches_nothing()
    {
        // Kubernetes reads a selector-less service as one whose endpoints are managed by hand, so
        // matching every pod in the namespace would be the opposite of the truth.
        var pods = new[] { Pod("api-1", labels: Labels(("app", "api"))) };

        Assert.Empty(PodMatching.SelectedBy(pods, Service("external", new Dictionary<string, string>())));
    }

    [Fact]
    public void A_service_does_not_reach_across_namespaces()
    {
        var pods = new[]
        {
            Pod("api-1", ns: "app", labels: Labels(("app", "api"))),
            Pod("api-1", ns: "staging", labels: Labels(("app", "api"))),
        };

        Assert.Single(PodMatching.SelectedBy(pods, Service("api", Labels(("app", "api")), ns: "app")));
    }

    [Fact]
    public void A_matching_value_has_to_be_the_same_value()
    {
        var pods = new[] { Pod("api-1", labels: Labels(("app", "API"))) };

        Assert.Empty(PodMatching.SelectedBy(pods, Service("api", Labels(("app", "api")))));
    }
}
