using Kontena.Sdk.Models;
using Xunit;
using Kontena.Core.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// Naming a backend (KON-119). The name is shown in six places from one resolver, so the rules about
/// when an override applies are the whole feature.
/// </summary>
public class BackendNamingTests
{
    private static readonly KontenaSettings Empty = new();

    [Fact]
    public void Without_an_override_the_source_name_is_used()
    {
        Assert.Equal("gke_prod_eu", Empty.NameFor("kubernetes:gke_prod_eu", "gke_prod_eu"));
    }

    [Fact]
    public void An_override_wins()
    {
        var settings = Empty.WithBackendName("kubernetes:gke_prod_eu", "Production EU", "gke_prod_eu");
        Assert.Equal("Production EU", settings.NameFor("kubernetes:gke_prod_eu", "gke_prod_eu"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Clearing_the_field_goes_back_to_the_source_name(string? cleared)
    {
        var named = Empty.WithBackendName("docker", "Work laptop", "Docker");
        var reset = named.WithBackendName("docker", cleared, "Docker");

        Assert.Equal("Docker", reset.NameFor("docker", "Docker"));
        Assert.Empty(reset.BackendNames);
    }

    [Fact]
    public void A_name_equal_to_the_source_is_not_stored()
    {
        // Storing it would freeze the name: the backend would keep saying "Docker" after the source
        // started calling itself something else, and nobody would know why.
        var settings = Empty.WithBackendName("docker", "Docker", "Docker");
        Assert.Empty(settings.BackendNames);
    }

    [Fact]
    public void Surrounding_whitespace_is_not_part_of_the_name()
    {
        var settings = Empty.WithBackendName("docker", "  Work laptop  ", "Docker");
        Assert.Equal("Work laptop", settings.NameFor("docker", "Docker"));
    }

    [Fact]
    public void A_whitespace_only_stored_name_still_falls_back()
    {
        // Not reachable through WithBackendName, but a hand-edited settings file can hold it, and a
        // blank entry in the switcher would be worse than the ugly original.
        var settings = Empty with { BackendNames = new Dictionary<string, string> { ["docker"] = "   " } };
        Assert.Equal("Docker", settings.NameFor("docker", "Docker"));
    }

    [Fact]
    public void Names_are_per_backend_id()
    {
        var settings = Empty
            .WithBackendName("kubernetes:default", "Work", "default")
            .WithBackendName("kubernetes@a1b2c3d4:default", "Client", "default");

        Assert.Equal("Work", settings.NameFor("kubernetes:default", "default"));
        Assert.Equal("Client", settings.NameFor("kubernetes@a1b2c3d4:default", "default"));
    }

    [Fact]
    public void Pruning_drops_names_for_backends_that_are_gone()
    {
        // A context removed from a kubeconfig leaves an entry behind. Harmless once; not if the file
        // only ever grows.
        var settings = Empty
            .WithBackendName("docker", "Work laptop", "Docker")
            .WithBackendName("kubernetes:old-cluster", "Retired", "old-cluster");

        var pruned = settings.PruneBackendNames(["docker"]);

        Assert.Equal("Work laptop", pruned.NameFor("docker", "Docker"));
        Assert.Single(pruned.BackendNames);
    }

    [Fact]
    public void Pruning_nothing_returns_the_same_settings()
    {
        // Called on every rename and every rebuild; it must not churn the file when there is no change.
        var settings = Empty.WithBackendName("docker", "Work laptop", "Docker");
        Assert.Same(settings, settings.PruneBackendNames(["docker", "podman"]));
    }
}
