using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Which clusters belong in the switcher (KON-120). The three states — chosen, declined, never seen —
/// are the feature, and the migration is the part that can quietly ruin someone's setup.
/// </summary>
public class ClusterVisibilityTests
{
    private const string Prod = "kubernetes:gke_prod";
    private const string Staging = "kubernetes:staging";
    private const string Toy = "kubernetes:kind-kind";

    [Fact]
    public void A_cluster_nobody_chose_is_not_shown()
    {
        Assert.False(new KontenaSettings().ShowsCluster(Prod));
    }

    [Fact]
    public void A_chosen_cluster_is_shown_and_a_declined_one_is_not()
    {
        var settings = new KontenaSettings().WithCluster(Prod, shown: true).WithCluster(Toy, shown: false);

        Assert.True(settings.ShowsCluster(Prod));
        Assert.False(settings.ShowsCluster(Toy));
    }

    [Fact]
    public void A_declined_cluster_is_not_offered_again()
    {
        // The difference between "declined" and "never seen". A list of wanted clusters could not tell
        // them apart, and would ask about the same context on every launch after the user said no.
        var settings = new KontenaSettings().WithCluster(Toy, shown: false);

        Assert.Empty(settings.NewClusters([Toy]));
        Assert.Equal([Prod], settings.NewClusters([Toy, Prod]));
    }

    [Fact]
    public void Everything_is_new_when_nothing_has_been_seen()
    {
        Assert.Equal([Prod, Staging], new KontenaSettings().NewClusters([Prod, Staging]));
    }

    [Fact]
    public void An_existing_installation_keeps_the_clusters_it_had()
    {
        // The migration. Before this change every discovered context was in the switcher; updating
        // Kontena must not empty it. That would be a regression dressed up as a feature.
        var existing = new KontenaSettings { Onboarded = true };

        var migrated = existing.AdoptExistingClusters([Prod, Staging, Toy]);

        Assert.True(migrated.ShowsCluster(Prod));
        Assert.True(migrated.ShowsCluster(Staging));
        Assert.True(migrated.ShowsCluster(Toy));
        Assert.Empty(migrated.NewClusters([Prod, Staging, Toy]));
    }

    [Fact]
    public void A_fresh_installation_adopts_nothing()
    {
        // Nothing to preserve, and its user should be the one choosing.
        var fresh = new KontenaSettings { Onboarded = false };

        var migrated = fresh.AdoptExistingClusters([Prod, Staging]);

        Assert.Empty(migrated.KnownClusters);
        Assert.Equal([Prod, Staging], migrated.NewClusters([Prod, Staging]));
    }

    [Fact]
    public void Adoption_happens_once()
    {
        // Otherwise a context the user declined would be adopted back on the next launch.
        var settings = new KontenaSettings { Onboarded = true }
            .WithCluster(Toy, shown: false);

        var again = settings.AdoptExistingClusters([Toy, Prod]);

        Assert.False(again.ShowsCluster(Toy));
        Assert.Equal([Prod], again.NewClusters([Toy, Prod]));
    }

    [Fact]
    public void Adopting_nothing_leaves_the_settings_alone()
    {
        var settings = new KontenaSettings { Onboarded = true };
        Assert.Same(settings, settings.AdoptExistingClusters([]));
    }

    [Fact]
    public void Clusters_that_left_the_kubeconfig_are_pruned()
    {
        var settings = new KontenaSettings()
            .WithCluster(Prod, shown: true)
            .WithCluster(Toy, shown: false);

        var pruned = settings.PruneClusters([Prod]);

        Assert.True(pruned.ShowsCluster(Prod));
        Assert.Single(pruned.KnownClusters);
    }

    [Fact]
    public void Pruning_nothing_returns_the_same_settings()
    {
        // Runs on every launch and every rebuild; it must not churn the file for no reason.
        var settings = new KontenaSettings().WithCluster(Prod, shown: true);
        Assert.Same(settings, settings.PruneClusters([Prod, Staging]));
    }

    [Fact]
    public void A_choice_can_be_changed()
    {
        var settings = new KontenaSettings()
            .WithCluster(Prod, shown: true)
            .WithCluster(Prod, shown: false);

        Assert.False(settings.ShowsCluster(Prod));
        Assert.Empty(settings.NewClusters([Prod]));
    }
}
