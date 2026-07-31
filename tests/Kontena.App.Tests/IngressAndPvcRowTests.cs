using Kontena.App.ViewModels;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The two grids behind Ingresses and Volume claims (KON-247). Both contracts existed and were
/// implemented in the adapter well before either had a screen, so what is worth pinning here is the
/// projection: what a cell says when the field is absent, and that the tooltip never contradicts the
/// cell it explains.
/// </summary>
public sealed class IngressAndPvcRowTests
{
    private static IngressRow Row(
        IReadOnlyList<IngressRule>? rules = null,
        IReadOnlyList<string>? addresses = null,
        IReadOnlyList<string>? tlsHosts = null,
        string @class = "nginx") =>
        new(new Ingress
        {
            Name = "web",
            Namespace = "app",
            Class = @class,
            Rules = rules ?? [],
            Addresses = addresses ?? [],
            TlsHosts = tlsHosts ?? [],
        });

    [Fact]
    public void A_host_serving_several_paths_appears_once_in_the_cell()
    {
        // The column answers "which names reach this ingress", and a host repeated once per path
        // fills the cell with the same word three times.
        var row = Row([
            new IngressRule("app.example.com", "/", "web", 80),
            new IngressRule("app.example.com", "/api", "api", 8080),
            new IngressRule("admin.example.com", "/", "admin", 80),
        ]);

        Assert.Equal("app.example.com  admin.example.com", row.Hosts);
    }

    [Fact]
    public void The_tooltip_keeps_every_rule_with_its_path_and_backend()
    {
        // What the cell drops is exactly what you came for once the host is not the question.
        var row = Row([
            new IngressRule("app.example.com", "/", "web", 80),
            new IngressRule("app.example.com", "/api", "api", 8080),
        ]);

        Assert.Equal(
            "app.example.com/ → web:80\napp.example.com/api → api:8080",
            row.HostsTooltip);
    }

    [Fact]
    public void A_rule_without_a_host_is_the_catch_all_and_says_so()
    {
        // An empty host in the API means "any host"; rendering it as an empty cell reads as missing
        // data rather than as the wildcard it is.
        var row = Row([new IngressRule(string.Empty, "/", "web", 80)]);

        Assert.Equal("*", row.Hosts);
        Assert.Equal("*/ → web:80", row.HostsTooltip);
    }

    [Fact]
    public void An_ingress_with_no_rules_is_flagged_rather_than_dashed()
    {
        // A rules block that never matched is a real and common mistake, and nothing else on the row
        // would say so.
        var row = Row();

        Assert.True(row.HasNoRules);
        Assert.Equal("—", row.Hosts);
        Assert.Null(row.HostsTooltip);
    }

    [Fact]
    public void Tls_names_the_hosts_it_covers_rather_than_claiming_the_ingress_is_covered()
    {
        // Three hosts with one of them in the TLS block is the case worth seeing, and a bare "TLS ✓"
        // is precisely the rendering that hides it.
        var row = Row(
            rules:
            [
                new IngressRule("app.example.com", "/", "web", 80),
                new IngressRule("admin.example.com", "/", "admin", 80),
            ],
            tlsHosts: ["app.example.com"]);

        Assert.True(row.HasTls);
        Assert.Equal("TLS: app.example.com", row.TlsTooltip);
    }

    [Fact]
    public void No_tls_means_no_chip_and_no_tooltip()
    {
        var row = Row([new IngressRule("app.example.com", "/", "web", 80)]);

        Assert.False(row.HasTls);
        Assert.Null(row.TlsTooltip);
    }

    [Fact]
    public void An_ingress_with_no_address_yet_reads_as_a_dash()
    {
        // Freshly created, or no controller watching its class: the address arrives later, and an
        // empty cell would look like a rendering fault.
        Assert.Equal("—", Row().Address);
        Assert.Null(Row().AddressTooltip);

        // One address needs no tooltip; the cell already holds it.
        Assert.Null(Row(addresses: ["34.120.55.10"]).AddressTooltip);
        Assert.Equal("34.120.55.10\n34.120.55.11", Row(addresses: ["34.120.55.10", "34.120.55.11"]).AddressTooltip);
    }

    [Fact]
    public void An_ingress_without_a_class_reads_as_a_dash()
    {
        Assert.Equal("—", Row(@class: string.Empty).Class);
    }

    // ── Volume claims ───────────────────────────────────────────────────────

    private static PvcRow Claim(PvcPhase phase, string volume = "pvc-8a1f", long capacity = 20L * 1024 * 1024 * 1024) =>
        new(new PersistentVolumeClaim
        {
            Name = "postgres-data",
            Namespace = "app",
            Phase = phase,
            Volume = volume,
            CapacityBytes = capacity,
            StorageClass = "standard-rwo",
            AccessModes = ["RWO"],
        });

    [Fact]
    public void A_pending_claim_says_what_to_go_and_look_at()
    {
        // The row cannot know whether the class is missing or has no provisioner, so it points at the
        // field rather than guessing between the two.
        var row = Claim(PvcPhase.Pending, volume: string.Empty, capacity: 0);

        Assert.True(row.IsPending);
        Assert.Contains("storage class", row.PendingHint, StringComparison.Ordinal);

        // Unbound: no volume, and no capacity granted to report.
        Assert.Equal("—", row.Volume);
        Assert.Equal("—", row.Capacity);
    }

    [Fact]
    public void A_bound_claim_carries_no_hint()
    {
        // A hint on every row is a hint nobody reads.
        var row = Claim(PvcPhase.Bound);

        Assert.False(row.IsPending);
        Assert.Null(row.PendingHint);
        Assert.Equal("pvc-8a1f", row.Volume);
        Assert.Equal("20Gi", row.Capacity);
    }

    [Fact]
    public void Capacity_is_stated_the_way_the_claim_was_written()
    {
        // A claim asking for 20Gi renders as "20Gi", not as the 21.5 GB the decimal formatter would
        // give it. The column is read next to a kubectl output, and disagreeing with it there reads
        // as Kontena having the wrong number rather than a different unit.
        Assert.Equal("20Gi", Claim(PvcPhase.Bound, capacity: 20L * 1024 * 1024 * 1024).Capacity);
        Assert.Equal("500Mi", Claim(PvcPhase.Bound, capacity: 500L * 1024 * 1024).Capacity);
        Assert.Equal("1.5Gi", Claim(PvcPhase.Bound, capacity: 1536L * 1024 * 1024).Capacity);
        Assert.Equal("1Ti", Claim(PvcPhase.Bound, capacity: 1024L * 1024 * 1024 * 1024).Capacity);
    }

    [Fact]
    public void Every_phase_is_rendered_by_its_own_name()
    {
        // Lost is rare enough that it is worth pinning it renders at all rather than falling into a
        // default that reads as Pending.
        Assert.Equal("Lost", Claim(PvcPhase.Lost).Status);
        Assert.Equal("Bound", Claim(PvcPhase.Bound).Status);
        Assert.Equal("Pending", Claim(PvcPhase.Pending).Status);
    }
}
