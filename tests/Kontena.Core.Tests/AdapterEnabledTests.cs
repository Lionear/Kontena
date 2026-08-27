using Kontena.Core.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// Which adapters are switched off is stored as deviations only, so an adapter added in a later release
/// arrives on rather than off (KON-283).
/// </summary>
public sealed class AdapterEnabledTests
{
    [Fact]
    public void An_adapter_nobody_has_touched_is_enabled()
    {
        Assert.True(new KontenaSettings().IsAdapterEnabled("docker"));
    }

    [Fact]
    public void Switching_one_off_records_it()
    {
        var settings = new KontenaSettings().WithAdapterEnabled("podman", enabled: false);

        Assert.False(settings.IsAdapterEnabled("podman"));
        Assert.Equal(["podman"], settings.DisabledAdapters);
    }

    [Fact]
    public void Switching_it_back_on_removes_the_entry_rather_than_recording_a_yes()
    {
        var settings = new KontenaSettings()
            .WithAdapterEnabled("podman", enabled: false)
            .WithAdapterEnabled("podman", enabled: true);

        Assert.True(settings.IsAdapterEnabled("podman"));
        Assert.Empty(settings.DisabledAdapters);
    }

    [Fact]
    public void Switching_off_twice_stores_one_entry()
    {
        var settings = new KontenaSettings()
            .WithAdapterEnabled("podman", enabled: false)
            .WithAdapterEnabled("podman", enabled: false);

        Assert.Equal(["podman"], settings.DisabledAdapters);
    }

    [Fact]
    public void One_adapter_being_off_says_nothing_about_another()
    {
        var settings = new KontenaSettings().WithAdapterEnabled("podman", enabled: false);

        Assert.True(settings.IsAdapterEnabled("docker"));
        Assert.True(settings.IsAdapterEnabled("kubernetes"));
    }
}
