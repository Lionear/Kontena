using Kontena.Sdk;

namespace Kontena.App.Tests;

/// <summary>
/// The xUnit collection for every test that mutates <c>BackendCatalog.Plugins</c> — the static list
/// behind <c>BackendCatalog.PluginProviders</c> — and resets it via <c>ResetPluginProviders()</c> in
/// <c>Dispose</c>. xunit 2.9.3 parallelises across test classes by default, one class's <c>Dispose</c>
/// can clear the list mid-assertion in another, and both <see cref="PluginCatalogTests"/> and
/// <c>PluginConsentPromptTests</c> do exactly that (KON-279 final review, finding 3). Join this
/// collection — do not add a new one — if a test you are writing touches <c>BackendCatalog</c>'s plugin
/// state. Holds nothing itself: collection membership alone is what serialises the classes in it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class BackendCatalogPluginState
{
    public const string Name = "BackendCatalog plugin state";
}

[Collection(BackendCatalogPluginState.Name)]
public sealed class PluginCatalogTests : IDisposable
{
    public void Dispose() => BackendCatalog.ResetPluginProviders();

    private sealed class StubProvider(string backend) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => "S";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => throw new NotSupportedException();
    }

    [Fact]
    public void No_plugins_means_the_catalog_is_unchanged()
    {
        Assert.Empty(BackendCatalog.PluginProviders);
        Assert.DoesNotContain(BackendCatalog.Build(includeDemo: false), p => p.Backend == "stub");
    }

    [Fact]
    public void A_plugin_provider_appears_in_the_catalog()
    {
        BackendCatalog.SetPluginProviders("com.acme.stub", [new StubProvider("stub")]);

        Assert.Contains(BackendCatalog.Build(includeDemo: false), p => p.Backend == "stub");
    }

    [Fact]
    public void Plugin_providers_come_after_the_local_engines()
    {
        BackendCatalog.SetPluginProviders("com.acme.stub", [new StubProvider("stub")]);

        var built = BackendCatalog.Build(includeDemo: false);

        Assert.True(
            built.FindIndex(p => p.Backend == "stub") > built.FindIndex(p => p.Backend == "podman"),
            "A plugin should list below the engines on this machine.");
    }

    [Fact]
    public void Setting_providers_again_adds_rather_than_replaces()
    {
        BackendCatalog.SetPluginProviders("com.acme.first", [new StubProvider("first")]);
        BackendCatalog.SetPluginProviders("com.acme.second", [new StubProvider("second")]);

        var built = BackendCatalog.Build(includeDemo: false);

        Assert.Contains(built, p => p.Backend == "first");
        Assert.Contains(built, p => p.Backend == "second");
    }

    [Fact]
    public void The_same_backend_is_not_added_twice()
    {
        BackendCatalog.SetPluginProviders("com.acme.stub", [new StubProvider("stub")]);
        BackendCatalog.SetPluginProviders("com.acme.stub", [new StubProvider("stub")]);

        Assert.Single(BackendCatalog.Build(includeDemo: false), p => p.Backend == "stub");
    }
}
