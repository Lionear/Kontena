using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The order the switcher lists connections in, as the user arranged it (KON-410).
/// </summary>
public class BackendOrderTests
{
    [Fact]
    public void Without_a_stored_order_the_switcher_keeps_the_order_it_found_them_in() =>
        Assert.Equal(["docker", "podman", "apple"], new KontenaSettings().InBackendOrder(["docker", "podman", "apple"]));

    [Fact]
    public void Moving_a_connection_down_swaps_it_with_the_one_below_and_is_remembered()
    {
        var settings = new KontenaSettings().WithBackendMoved(["docker", "podman", "apple"], "docker", by: 1);

        Assert.Equal(["podman", "docker", "apple"], settings.InBackendOrder(["docker", "podman", "apple"]));
    }

    [Fact]
    public void A_connection_added_later_goes_after_the_ones_the_user_arranged()
    {
        // A new remote or kube-context has no place in the stored order yet; putting it first would
        // undo the arrangement the next time anything is added.
        var settings = new KontenaSettings().WithBackendMoved(["docker", "podman"], "podman", by: -1);

        Assert.Equal(["podman", "docker", "remote:new"], settings.InBackendOrder(["docker", "remote:new", "podman"]));
    }

    [Fact]
    public void Moving_past_either_end_changes_nothing()
    {
        var settings = new KontenaSettings();

        Assert.Same(settings, settings.WithBackendMoved(["docker", "podman"], "docker", by: -1));
        Assert.Same(settings, settings.WithBackendMoved(["docker", "podman"], "podman", by: 1));
    }

    [Fact]
    public void Arranging_one_group_leaves_the_other_groups_order_alone()
    {
        // Engines and clusters are listed apart; moving a cluster must not reset how the engines sit.
        var settings = new KontenaSettings()
            .WithBackendMoved(["docker", "podman"], "podman", by: -1)
            .WithBackendMoved(["kube:a", "kube:b"], "kube:b", by: -1);

        Assert.Equal(["podman", "docker"], settings.InBackendOrder(["docker", "podman"]));
        Assert.Equal(["kube:b", "kube:a"], settings.InBackendOrder(["kube:a", "kube:b"]));
    }
}
