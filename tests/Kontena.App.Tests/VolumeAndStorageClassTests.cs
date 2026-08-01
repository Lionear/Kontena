using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// PersistentVolumes and StorageClasses (KON-254). Rick, testing KON-246 against a real cluster:
/// "Alleen Volume Claims?" — and the half that was missing is the half you need when something is
/// wrong. What is pinned here is the wording that turns a state into an action, and the routes
/// between the three pages, which are the reason these screens are worth more than two extra lists.
/// </summary>
public sealed class VolumeAndStorageClassTests
{
    private static PersistentVolumeRow Volume(
        VolumePhase phase = VolumePhase.Bound,
        ReclaimPolicy reclaim = ReclaimPolicy.Delete,
        string claim = "app/postgres-data",
        Action<string>? onOpenClaim = null) =>
        new(new PersistentVolume
        {
            Name = "pvc-8a1f",
            Phase = phase,
            ReclaimPolicy = reclaim,
            Claim = claim,
            CapacityBytes = 20L * 1024 * 1024 * 1024,
            StorageClass = "standard-rwo",
            Driver = "pd.csi.storage.gke.io",
        }, onOpenClaim);

    [Fact]
    public void Released_with_Retain_is_called_out_for_what_it_costs()
    {
        // The claim is gone, the data is not, and nothing will reuse the volume until a person deals
        // with it. Every other phase either resolves itself or is already being looked at.
        var row = Volume(VolumePhase.Released, ReclaimPolicy.Retain);

        Assert.True(row.HasNote);
        Assert.Contains("still paying for", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Released_with_Delete_says_something_different()
    {
        // Same phase, opposite meaning: this one is on its way out on its own.
        var row = Volume(VolumePhase.Released, ReclaimPolicy.Delete);

        Assert.True(row.HasNote);
        Assert.DoesNotContain("still paying for", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_healthy_volume_carries_no_note()
    {
        // A note on every row is a note nobody reads.
        Assert.False(Volume().HasNote);
        Assert.Null(Volume().Note);
    }

    [Fact]
    public void The_claim_column_routes_by_name_without_its_namespace()
    {
        // The cell shows "namespace/name" because a claim name alone is ambiguous; the route needs
        // the name on its own, because that is what the claims list is searched by.
        var opened = new List<string>();
        var row = Volume(onOpenClaim: opened.Add);

        Assert.True(row.CanOpenClaim);
        row.OpenClaimCommand.Execute(null);

        Assert.Equal("postgres-data", Assert.Single(opened));
        Assert.Equal("app/postgres-data", row.Claim);
    }

    [Fact]
    public void An_unbound_volume_offers_no_route()
    {
        // A link that opens an empty list is worse than plain text.
        Assert.False(Volume(VolumePhase.Available, claim: string.Empty, onOpenClaim: _ => { }).CanOpenClaim);
    }

    [Fact]
    public void Capacity_is_stated_the_way_kubernetes_states_it()
    {
        Assert.Equal("20Gi", Volume().Capacity);
    }

    // ── Storage classes ─────────────────────────────────────────────────────

    private static StorageClassRow Class(
        VolumeBindingMode binding = VolumeBindingMode.Immediate,
        string provisioner = "pd.csi.storage.gke.io",
        bool isDefault = false) =>
        new(new StorageClass
        {
            Name = "standard-rwo",
            Provisioner = provisioner,
            BindingMode = binding,
            IsDefault = isDefault,
        });

    [Fact]
    public void WaitForFirstConsumer_is_said_in_words_that_reach_the_conclusion()
    {
        // The API's own word is the single most common reason someone believes their storage is
        // broken when it is working exactly as designed. Repeating the word does not help them.
        var row = Class(VolumeBindingMode.WaitForFirstConsumer);

        Assert.Equal("When a pod needs it", row.Binding);
        Assert.Contains("not a fault", row.BindingDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Immediate_says_so_plainly()
    {
        Assert.Equal("As soon as a claim exists", Class().Binding);
    }

    [Fact]
    public void A_class_with_no_provisioner_is_flagged_either_way_it_got_there()
    {
        // A legitimate configuration — volumes made by hand — and also what a typo produces. The row
        // cannot tell which, so it states the consequence rather than guessing the cause.
        Assert.True(Class(provisioner: "kubernetes.io/no-provisioner").NoProvisioner);
        Assert.True(Class(provisioner: string.Empty).NoProvisioner);
        Assert.False(Class().NoProvisioner);
    }

    [Fact]
    public void The_default_class_is_marked()
    {
        Assert.True(Class(isDefault: true).IsDefault);
        Assert.False(Class().IsDefault);
    }

    // ── The claim's side of the routes ──────────────────────────────────────

    [Fact]
    public void A_bound_claim_routes_to_its_volume_and_its_class()
    {
        var volumes = new List<string>();
        var classes = new List<string>();
        var row = new PvcRow(
            new PersistentVolumeClaim
            {
                Name = "postgres-data", Namespace = "app", Phase = PvcPhase.Bound,
                Volume = "pvc-8a1f", StorageClass = "standard-rwo",
            },
            volumes.Add, classes.Add);

        Assert.True(row.CanOpenVolume);
        Assert.True(row.CanOpenClass);

        row.OpenVolumeCommand.Execute(null);
        row.OpenClassCommand.Execute(null);

        Assert.Equal("pvc-8a1f", Assert.Single(volumes));
        Assert.Equal("standard-rwo", Assert.Single(classes));
    }

    [Fact]
    public void A_pending_claim_has_no_volume_to_go_to_but_still_has_a_class()
    {
        // Which is the whole point: the class is where the reason lives, and the claim is Pending
        // precisely because there is no volume yet.
        var row = new PvcRow(
            new PersistentVolumeClaim
            {
                Name = "cache-data", Namespace = "app", Phase = PvcPhase.Pending, StorageClass = "local-path",
            },
            _ => { }, _ => { });

        Assert.False(row.CanOpenVolume);
        Assert.True(row.CanOpenClass);
        Assert.Contains("storage class", row.PendingHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fake_cluster_serves_both_kinds()
    {
        var engine = new FakeClusterEngine();

        Assert.NotEmpty(await engine.ListVolumesAsync());
        Assert.Contains(await engine.ListStorageClassesAsync(), c => c.IsDefault);
    }
}
