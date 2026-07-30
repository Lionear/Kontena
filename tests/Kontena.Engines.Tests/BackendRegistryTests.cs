using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Engines.Fakes;
using Xunit;
using Kontena.Engines;

namespace Kontena.Engines.Tests;

public class BackendRegistryTests
{
    [Fact]
    public async Task Probes_a_working_provider_as_connected()
    {
        var probe = await BackendRegistry.ProbeAsync(new FakeEngineProvider());

        Assert.True(probe.Connected);
        Assert.Equal("fake", probe.Provider.Backend);
    }

    [Fact]
    public async Task Probes_an_unreachable_provider_as_not_connected()
    {
        var probe = await BackendRegistry.ProbeAsync(new UnreachableProvider());

        Assert.False(probe.Connected);
        Assert.Equal("Not connected", probe.Detail);
    }

    [Fact]
    public async Task ProbeAll_returns_one_result_per_provider()
    {
        var registry = new BackendRegistry([new FakeEngineProvider(), new UnreachableProvider()]);

        var probes = await registry.ProbeAllAsync();

        Assert.Equal(2, probes.Count);
        Assert.Single(probes, p => p.Connected);
    }

    [Fact]
    public void Register_adds_a_provider()
    {
        var registry = new BackendRegistry([new FakeEngineProvider()]);
        registry.Register(new UnreachableProvider());

        Assert.Equal(2, registry.Providers.Count);
    }

    private sealed class UnreachableProvider : IBackendProvider
    {
        public string Backend => "dead";
        public string DisplayName => "Dead";
        public string Chip => "X";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => throw new EngineUnreachableException("no engine");
    }
}
