using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.TestPlugin;

/// <summary>
/// A real plugin in a real assembly, for the loader tests. It is deliberately not a working engine:
/// what is under test is that the host finds it, agrees to it, loads it in its own context, and gets
/// back a provider whose interface type is the host's — not that it can list containers.
/// </summary>
public sealed class TestPlugin : IEnginePlugin
{
    public EngineManifest Manifest => new()
    {
        Id = "com.kontena.test",
        Name = "Test Plugin",
        Version = "1.0.0",
        Author = "Kontena",
        Description = "Fixture for the plugin loader tests.",
        MinSdkVersion = "0.1.0",
    };

    public IEnumerable<IBackendProvider> GetProviders() => [new TestProvider()];
}

/// <summary>The one provider the fixture contributes. Never connects.</summary>
public sealed class TestProvider : IBackendProvider
{
    public string Backend => "testplugin";
    public string DisplayName => "Test Plugin";
    public string Chip => "T";
    public BackendKind Kind => BackendKind.Engine;

    public IBackend CreateBackend() => new TestBackend();
}

/// <summary>A backend that is always unreachable — the loader never pings it, the registry might.</summary>
public sealed class TestBackend : IBackend
{
    public string Backend => "testplugin";

    public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("The test plugin has no engine behind it.");

    public ValueTask PingAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("The test plugin has no engine behind it.");
}
