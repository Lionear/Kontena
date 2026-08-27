using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.HostilePlugin;

/// <summary>
/// A real plugin in a real assembly whose provider throws from an identity getter — the shape of a
/// plugin that would otherwise take down startup (KON-279 final review, finding 1). It gets its own
/// assembly rather than sharing one with <c>Kontena.TestPlugin</c>: <c>PluginLoader.Load</c> picks the
/// plugin type with <c>FirstOrDefault</c> over exported types, so two <see cref="IEnginePlugin"/> types
/// in one assembly would make which one is found unpredictable.
/// </summary>
public sealed class HostilePlugin : IEnginePlugin
{
    public EngineManifest Manifest => new()
    {
        Id = "com.kontena.hostile",
        Name = "Hostile Plugin",
        Version = "1.0.0",
        Author = "Kontena",
        Description = "Fixture whose provider throws from an identity getter.",
        MinSdkVersion = "0.1.0",
        Backends = [BackendKind.Engine],
    };

    public IEnumerable<IBackendProvider> GetProviders() => [new HostileProvider()];
}

/// <summary>
/// A provider whose <see cref="DisplayName"/> throws — standing in for a getter that formats a field
/// come back null from the tool it shells out to. The loader must catch this while it still can, not
/// let it surface the first time the host reads the property.
/// </summary>
public sealed class HostileProvider : IBackendProvider
{
    public string Backend => "hostileplugin";

    // Stands in for the null-formatting NullReferenceException the finding describes — CA2201 forbids
    // throwing that type directly, and any exception proves the same point: the loader must catch this
    // before the host ever reads the property.
    public string DisplayName => throw new InvalidOperationException("boom");

    public string Chip => "H";
    public BackendKind Kind => BackendKind.Engine;

    public IBackend CreateBackend() => throw new InvalidOperationException("The hostile fixture has no engine behind it.");
}
