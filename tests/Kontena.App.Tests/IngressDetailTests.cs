using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The ingress detail page (KON-453). Reported by a user: an ingress was the one kind whose row went
/// nowhere — no YAML, no fields — while every other kind had a drawer with both.
/// <para>
/// What is worth pinning is the projection, the same way <c>IngressAndPvcRowTests</c> pins the grid's:
/// the rules the list could only fit in a tooltip, which certificate covers which host, and that the
/// page keeps what it has when a re-read comes back without it (the <c>RefetchAsync</c> contract from
/// KON-450). The refreshes are driven directly rather than through a watch, for the reason written up
/// in KON-451.
/// </para>
/// </summary>
public sealed class IngressDetailTests
{
    private static Ingress Ingress(
        IReadOnlyList<IngressRule>? rules = null,
        IReadOnlyList<IngressTls>? tls = null,
        IngressBackend? defaultBackend = null,
        string @class = "nginx",
        string name = "web") =>
        new()
        {
            Name = name,
            Namespace = "app",
            Class = @class,
            Rules = rules ?? [],
            Tls = tls ?? [],
            DefaultBackend = defaultBackend,
        };

    private static ClusterIngressDetailViewModel Detail(Ingress ingress) =>
        new(new FakeClusterEngine(), ingress);

    [Fact]
    public void Every_rule_gets_a_row_of_its_own_with_its_path_and_backend()
    {
        // The list cell collapses hosts to the distinct ones and puts the rest on hover — which is
        // the wrong place for the one thing an ingress is opened to read.
        using var detail = Detail(Ingress([
            new IngressRule("app.example.com", "/", "web", 80),
            new IngressRule("app.example.com", "/api", "api", 8080),
        ]));

        Assert.True(detail.HasRules);
        Assert.Equal(["app.example.com", "app.example.com"], detail.Rules.Select(r => r.Host));
        Assert.Equal(["/", "/api"], detail.Rules.Select(r => r.Path));
        Assert.Equal(["web:80", "api:8080"], detail.Rules.Select(r => r.Backend));
    }

    [Fact]
    public void A_rule_without_a_host_or_path_reads_the_same_as_it_does_on_the_list()
    {
        // Same substitutions as IngressRow, so the drawer and the row it was opened from cannot
        // describe one rule two ways.
        using var detail = Detail(Ingress([new IngressRule(string.Empty, string.Empty, "web", 80)]));

        var rule = Assert.Single(detail.Rules);
        Assert.Equal("*", rule.Host);
        Assert.Equal("/", rule.Path);
    }

    [Fact]
    public void An_ingress_with_no_rules_has_no_table_to_show()
    {
        // Routing nothing is a real and common mistake — a rules block that never matched, or a
        // service name with a typo in it — and the view says so where the table would be.
        using var detail = Detail(Ingress());

        Assert.False(detail.HasRules);
        Assert.Empty(detail.Rules);
    }

    [Fact]
    public void Tls_says_which_certificate_covers_which_hosts()
    {
        using var detail = Detail(Ingress(tls: [
            new IngressTls("web-tls", ["app.example.com", "www.example.com"]),
            new IngressTls("admin-tls", ["admin.example.com"]),
        ]));

        Assert.True(detail.HasTls);
        Assert.Equal(["web-tls", "admin-tls"], detail.Tls.Select(t => t.Secret));
        Assert.Equal("app.example.com  www.example.com", detail.Tls[0].Hosts);
    }

    [Fact]
    public void A_tls_block_with_no_secret_names_the_controllers_own_certificate()
    {
        // Legal, and it means something specific: the controller presents its default certificate.
        // An empty cell would read as a missing field.
        using var detail = Detail(Ingress(tls: [new IngressTls(string.Empty, ["app.example.com"])]));

        Assert.Equal("(controller default)", Assert.Single(detail.Tls).Secret);
    }

    [Fact]
    public void Without_tls_the_page_says_so_rather_than_showing_an_empty_table()
    {
        using var detail = Detail(Ingress());

        Assert.False(detail.HasTls);
    }

    [Fact]
    public void The_default_backend_shows_only_when_the_ingress_names_one()
    {
        using var withOne = Detail(Ingress(defaultBackend: new IngressBackend("web", 80)));
        using var without = Detail(Ingress());

        Assert.True(withOne.HasDefaultBackend);
        Assert.Equal("web:80", withOne.DefaultBackendText);

        // No default backend is not the same fact as one pointing nowhere: unmatched traffic gets the
        // controller's own 404, and there is nothing to name.
        Assert.False(without.HasDefaultBackend);
        Assert.Null(without.DefaultBackendText);
    }

    [Fact]
    public void An_ingress_without_a_class_shows_a_dash_rather_than_an_empty_chip()
    {
        using var detail = Detail(Ingress(@class: string.Empty));

        Assert.Equal("—", detail.ClassText);
    }

    [Fact]
    public void The_page_has_no_pods_tab()
    {
        // An ingress routes to services; which pods are behind those is the service page's question,
        // and a tab opening onto an empty list would be a worse answer than no tab.
        using var detail = Detail(Ingress());

        Assert.False(detail.ShowPodsTab);
    }

    [Fact]
    public async Task The_yaml_tab_opens_the_manifest_of_this_ingress()
    {
        // The whole of the report: "bij ingresses kan je de YAML niet inzien". Built on first visit
        // rather than in the constructor, so this drives the tab the way a click does.
        var engine = new FakeClusterEngine();
        var web = (await engine.ListIngressesAsync("app")).First(i => i.Name == "web");

        using var detail = new ClusterIngressDetailViewModel(engine, web);

        Assert.Null(detail.Yaml);

        detail.SelectTabCommand.Execute("yaml");

        Assert.NotNull(detail.Yaml);
        Assert.Equal("Ingress web", detail.Yaml.Title);

        for (var i = 0; i < 50 && detail.Yaml.IsLoading; i++)
            await Task.Delay(5);

        Assert.Contains("kind: Ingress", detail.Yaml.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_assigned_after_the_page_opened_reaches_it()
    {
        // The field worth the re-read: an ingress has no address until its controller assigns one,
        // and that arrival is exactly the Modified event the base class hands to RefreshAsync.
        var engine = new FakeClusterEngine();
        var stale = Ingress(name: "web");

        using var detail = new ClusterIngressDetailViewModel(engine, stale);

        Assert.Equal("—", detail.AddressText);
        Assert.False(detail.HasTls);

        await detail.RefreshAsync();

        // The cluster's own web ingress, read back through the same lister the grid uses.
        Assert.Equal("34.120.55.10", detail.AddressText);
        Assert.Equal("app.example.com", Assert.Single(detail.Rules).Host);
        Assert.Equal("web-tls", Assert.Single(detail.Tls).Secret);
    }

    [Fact]
    public async Task An_ingress_missing_from_the_read_leaves_the_page_as_it_was()
    {
        // The other half of RefetchAsync's contract (KON-450): an object missing from the answer is
        // one the same watch is about to report Deleted, and blanking the page in the meantime swaps
        // a few seconds of staleness for a screen of em-dashes.
        var engine = new FakeClusterEngine();
        var ghost = Ingress([new IngressRule("gone.example.com", "/", "gone", 80)], name: "not-in-this-cluster");

        using var detail = new ClusterIngressDetailViewModel(engine, ghost);

        await detail.RefreshAsync();

        Assert.Equal("gone.example.com", Assert.Single(detail.Rules).Host);
        Assert.Equal("nginx", detail.ClassText);
    }

    [Fact]
    public void The_detail_route_carries_the_ingress()
    {
        var opened = new List<Ingress>();
        var row = new IngressRow(Ingress(), onDelete: null, onOpenDetail: opened.Add);

        Assert.True(row.CanOpen);
        row.OpenCommand.Execute(null);

        Assert.Equal("web", Assert.Single(opened).Name);
    }

    [Fact]
    public void Without_a_wired_detail_route_the_name_is_not_a_link()
    {
        Assert.False(new IngressRow(Ingress()).CanOpen);
    }
}
