using Kontena.Sdk;

namespace Kontena.App.Tests;

/// <summary>
/// Plugin providers hang off the process, not off a call: BackendCatalog.Build runs again on every
/// settings change, and an AssemblyLoadContext is not something to enter twice for the same directory.
/// </summary>
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
        BackendCatalog.SetPluginProviders([new StubProvider("stub")]);

        Assert.Contains(BackendCatalog.Build(includeDemo: false), p => p.Backend == "stub");
    }

    [Fact]
    public void Plugin_providers_come_after_the_local_engines()
    {
        BackendCatalog.SetPluginProviders([new StubProvider("stub")]);

        var built = BackendCatalog.Build(includeDemo: false);

        Assert.True(
            built.FindIndex(p => p.Backend == "stub") > built.FindIndex(p => p.Backend == "podman"),
            "A plugin should list below the engines on this machine.");
    }

    [Fact]
    public void Setting_providers_again_adds_rather_than_replaces()
    {
        BackendCatalog.SetPluginProviders([new StubProvider("first")]);
        BackendCatalog.SetPluginProviders([new StubProvider("second")]);

        var built = BackendCatalog.Build(includeDemo: false);

        Assert.Contains(built, p => p.Backend == "first");
        Assert.Contains(built, p => p.Backend == "second");
    }

    [Fact]
    public void The_same_backend_is_not_added_twice()
    {
        BackendCatalog.SetPluginProviders([new StubProvider("stub")]);
        BackendCatalog.SetPluginProviders([new StubProvider("stub")]);

        Assert.Single(BackendCatalog.Build(includeDemo: false), p => p.Backend == "stub");
    }
}
