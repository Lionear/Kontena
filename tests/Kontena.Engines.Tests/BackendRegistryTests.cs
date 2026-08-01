using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
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

    /// <summary>
    /// The reason the deadline exists (KON-317): a provider that is not there can take seconds to
    /// say so — a Windows named pipe that does not exist is the case that started this — and the
    /// round costs whatever its slowest member costs. <see cref="HangingProvider"/> ignores the
    /// token on purpose, because a client that cooperated would not have been a problem.
    /// </summary>
    [Fact]
    public async Task A_provider_that_never_answers_is_not_connected_once_the_deadline_passes()
    {
        var started = DateTimeOffset.UtcNow;

        var probe = await BackendRegistry.ProbeAsync(new HangingProvider(), TimeSpan.FromMilliseconds(50));

        Assert.False(probe.Connected);
        Assert.Equal("Not connected", probe.Detail);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2), "the probe waited for the ping, not the deadline");
    }

    /// <summary>
    /// The whole point, at the level the app actually calls: one provider that never answers must not
    /// decide how long everyone waits. Costs <see cref="BackendRegistry.ProbeTimeout"/> to run, which
    /// is the bound being asserted.
    /// </summary>
    [Fact]
    public async Task A_provider_that_never_answers_does_not_hold_up_the_round()
    {
        var registry = new BackendRegistry([new FakeEngineProvider(), new HangingProvider()]);
        var started = DateTimeOffset.UtcNow;

        var probes = await registry.ProbeAllAsync();

        Assert.Equal(2, probes.Count);
        Assert.Single(probes, p => p.Connected);
        Assert.True(
            DateTimeOffset.UtcNow - started < BackendRegistry.ProbeTimeout * 2,
            "the round outlived its own deadline");
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

    /// <summary>An engine whose ping never returns and never notices cancellation.</summary>
    private sealed class HangingProvider : IBackendProvider
    {
        public string Backend => "hangs";
        public string DisplayName => "Hangs";
        public string Chip => "H";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new HangingEngine();

        private sealed class HangingEngine : IBackend
        {
            public string Backend => "hangs";

            public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
                throw new NotSupportedException("the ping never returns, so nothing gets this far");

            // CancellationToken.None on purpose: an engine that noticed the token would never have
            // needed a deadline in the first place.
            public ValueTask PingAsync(CancellationToken ct = default) =>
                new(Task.Delay(Timeout.Infinite, CancellationToken.None));
        }
    }
}
