using Kontena.Core.Versioning;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Which published product a backend's version belongs to. Wrong here means comparing an engine
/// against someone else's support dates, which is worse than saying nothing (KON-370).
/// </summary>
public class BackendProductsTests
{
    [Theory]
    [InlineData("docker", "docker-engine")]
    [InlineData("podman", "podman")]
    [InlineData("kubernetes", "kubernetes")]
    public void A_known_backend_maps_to_its_published_product(string backend, string product) =>
        Assert.Equal(product, BackendProducts.For(backend));

    [Fact]
    public void Nerdctl_is_measured_as_containerd()
    {
        // NerdctlEngine reports containerd's ServerVersion, not nerdctl's — nerdctl does not name
        // itself in that payload at all. Measuring it against nerdctl's releases would compare two
        // different products' numbers.
        Assert.Equal("containerd", BackendProducts.For("nerdctl:default"));
    }

    [Fact]
    public void A_remote_engine_is_still_Docker_Engine()
    {
        // A remote appears as its own backend ("docker-remote:<id>") because it is its own entry in the
        // switcher — but it is Docker on someone's server, which is exactly where a daemon gets
        // forgotten for two years.
        Assert.Equal("docker-engine", BackendProducts.For("docker-remote:7f3a"));
    }

    [Fact]
    public void A_backend_id_carrying_a_context_still_maps()
    {
        // Ids grow a suffix once there is more than one of something: "nerdctl:<namespace>",
        // "kubernetes:<context>", a remote engine's host.
        Assert.Equal("kubernetes", BackendProducts.For("kubernetes:prod-eu-west"));
    }

    [Fact]
    public void A_backend_nobody_publishes_a_calendar_for_maps_to_nothing()
    {
        // Apple's `container` has no entry at endoflife.date. Silence is the honest answer.
        Assert.Null(BackendProducts.For("apple"));
        Assert.Null(BackendProducts.For(""));
        Assert.Null(BackendProducts.For(null));
    }
}
