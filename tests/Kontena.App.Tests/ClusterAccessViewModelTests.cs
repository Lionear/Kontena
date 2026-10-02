using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>The access page: bindings joined to the roles they grant (KON-474).</summary>
public sealed class ClusterAccessViewModelTests
{
    private static async Task<ClusterAccessViewModel> PageAsync(string? ns = null)
    {
        var page = new ClusterAccessViewModel(new FakeClusterEngine(), ns);
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task Each_subject_of_a_binding_is_its_own_grant()
    {
        using var page = await PageAsync();

        var grants = page.Items.Where(r => r.Binding == "ci-view").ToArray();
        Assert.Equal(["ci", "jane@example.com"], grants.Select(g => g.Subject).Order());
        Assert.Contains(grants, g => g.SubjectDetail == "ServiceAccount · namespace app");
    }

    /// <summary>
    /// A RoleBinding that names a ClusterRole gets that role's rules, but only in its own namespace —
    /// the case that makes the Resources browser's separate tables hard to read.
    /// </summary>
    [Fact]
    public async Task A_role_binding_to_a_cluster_role_carries_its_rules_but_its_own_scope()
    {
        using var page = await PageAsync();

        var ci = page.Items.Single(r => r.Subject == "ci");
        Assert.Equal(("view", "ClusterRole", "app"), (ci.Role, ci.RoleKind, ci.Scope));
        Assert.Contains("deployments.apps", ci.Rules.Single().Resources);
        Assert.Equal("get, list, watch", ci.Rules.Single().Verbs);
    }

    [Fact]
    public async Task A_binding_to_a_role_that_does_not_exist_says_so()
    {
        using var page = await PageAsync();

        var ops = page.Items.Single(r => r.Subject == "ops");
        Assert.True(ops.RoleMissing);
        Assert.Empty(ops.Rules);
        Assert.Equal("role not found", ops.RulesLabel);
    }

    /// <summary>A ClusterRoleBinding grants in every namespace, so a namespace's view includes it.</summary>
    [Fact]
    public async Task A_namespace_shows_its_own_bindings_and_the_cluster_wide_ones()
    {
        using var page = await PageAsync("app");

        Assert.Contains(page.Items, r => r.Scope == "Cluster-wide" && r.Subject == "system:masters");
        Assert.Contains(page.Items, r => r.Binding == "read-config");
        Assert.DoesNotContain(page.Items, r => r.Scope == "monitoring");
    }

    [Fact]
    public async Task Searching_for_a_resource_finds_who_can_touch_it()
    {
        using var page = await PageAsync();

        page.SearchText = "secrets";

        var web = Assert.Single(page.Items);
        Assert.Equal("web", web.Subject);
        Assert.Equal("only web-tls", web.Rules.Single(r => r.Resources == "secrets").Names);
    }

    [Fact]
    public void Wildcards_and_non_resource_urls_are_shown_as_written()
    {
        var rule = new AccessGrantRow(
            new AccessSubject("Group", "system:masters"),
            new AccessBinding { Name = "cluster-admin", RoleKind = "ClusterRole", RoleName = "cluster-admin" },
            new AccessRole
            {
                Name = "cluster-admin",
                Rules =
                [
                    new AccessRule { Verbs = ["*"], ApiGroups = ["*"], Resources = ["*"] },
                    new AccessRule { Verbs = ["get"], NonResourceUrls = ["/healthz", "/version"] },
                ],
            });

        Assert.Equal("*", rule.Rules[0].Resources);
        Assert.Equal("/healthz, /version", rule.Rules[1].Resources);
        Assert.Equal("Cluster-wide", rule.Scope);
    }
}
