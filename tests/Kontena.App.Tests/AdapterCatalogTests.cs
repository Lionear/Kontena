using Kontena.Adapters.Apple;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.Adapters.Podman;
using Kontena.Engines.Plugins;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The list Settings › Extensions is built from, and the filter that keeps a switched-off adapter out
/// of the switcher (KON-283).
/// </summary>
[Collection(BackendCatalogPluginState.Name)]
public sealed class AdapterCatalogTests : IDisposable
{
    public void Dispose() => BackendCatalog.ResetPluginProviders();

    private sealed class StubProvider(string backend, BackendKind kind = BackendKind.Engine) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => "S";
        public BackendKind Kind => kind;
        public IBackend CreateBackend() => throw new NotSupportedException();
    }

    private static DiscoveredPlugin Loaded(
        string id, string name, params IBackendProvider[] providers) =>
        new(
            Directory: Path.Combine(Path.GetTempPath(), id),
            Manifest: new PluginManifest
            {
                Id = id,
                Name = name,
                Version = "1.2.0",
                Assembly = "Plugin.dll",
                Author = "Acme",
                Description = "A plugin.",
            },
            Status: PluginStatus.Loaded,
            Reason: null,
            Providers: providers);

    [Fact]
    public void Every_bundled_adapter_is_listed()
    {
        var ids = AdapterCatalog.All([]).Select(a => a.Id).ToList();

        Assert.Contains(DockerAdapterModule.BackendId, ids);
        Assert.Contains(PodmanAdapterModule.BackendId, ids);
        Assert.Contains(KubernetesAdapterModule.BackendId, ids);
    }

    /// <summary>
    /// Apple's runtime exists only on macOS, so it is left out elsewhere rather than shown switched off —
    /// a Windows machine has no decision to make about it (KON-280 will move this to the manifest).
    /// </summary>
    [Fact]
    public void An_adapter_that_cannot_run_here_is_not_listed()
    {
        var listed = AdapterCatalog.All([]).Any(a => a.Id == AppleAdapterModule.BackendId);

        Assert.Equal(OperatingSystem.IsMacOS(), listed);
    }

    [Fact]
    public void A_loaded_plugin_is_listed_beside_the_bundled_ones()
    {
        var adapters = AdapterCatalog.All([Loaded("com.acme.nerdctl", "nerdctl", new StubProvider("nerdctl:default"))]);

        var plugin = Assert.Single(adapters, a => a.Id == "com.acme.nerdctl");
        Assert.False(plugin.IsBundled);
        Assert.Equal("nerdctl", plugin.Manifest.Name);
    }

    /// <summary>A plugin awaiting consent has a different question outstanding, so it gets no switch.</summary>
    [Fact]
    public void A_plugin_that_did_not_load_is_not_listed()
    {
        var pending = Loaded("com.acme.nerdctl", "nerdctl") with { Status = PluginStatus.AwaitingConsent };

        Assert.DoesNotContain(AdapterCatalog.All([pending]), a => a.Id == "com.acme.nerdctl");
    }

    [Fact]
    public void A_plugin_contributing_no_backend_is_a_tool()
    {
        var adapters = AdapterCatalog.All([Loaded("com.acme.studio", "Manifest Studio")]);

        var studio = Assert.Single(adapters, a => a.Id == "com.acme.studio");
        Assert.Equal(AdapterContribution.Tool, studio.Contribution);
    }

    [Fact]
    public void A_plugin_contributing_a_cluster_backend_is_an_orchestrator()
    {
        var adapters = AdapterCatalog.All(
            [Loaded("com.acme.nomad", "Nomad", new StubProvider("nomad", BackendKind.Cluster))]);

        Assert.Equal(AdapterContribution.Orchestrator, Assert.Single(adapters, a => a.Id == "com.acme.nomad").Contribution);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("docker-remote:abc123")]
    public void The_docker_adapter_owns_its_local_and_remote_backends(string backend)
    {
        var owner = AdapterCatalog.OwnerOf(AdapterCatalog.All([]), backend);

        Assert.Equal(DockerAdapterModule.BackendId, owner?.Id);
    }

    [Fact]
    public void The_kubernetes_adapter_owns_every_context()
    {
        var owner = AdapterCatalog.OwnerOf(AdapterCatalog.All([]), "kubernetes:prod-eu-west");

        Assert.Equal(KubernetesAdapterModule.BackendId, owner?.Id);
    }

    [Fact]
    public void A_backend_no_adapter_claims_has_no_owner()
    {
        Assert.Null(AdapterCatalog.OwnerOf(AdapterCatalog.All([]), "fake"));
    }

    // ── The filter ──────────────────────────────────────────────────────────

    [Fact]
    public void A_switched_off_adapter_contributes_no_providers()
    {
        var built = BackendCatalog.Build(
            includeDemo: false,
            adapterEnabled: id => id != PodmanAdapterModule.BackendId);

        Assert.DoesNotContain(built, p => p.Backend == PodmanAdapterModule.BackendId);
        Assert.Contains(built, p => p.Backend == DockerAdapterModule.BackendId);
    }

    /// <summary>
    /// A remote is reached by speaking the Docker Engine API at another host, so it goes with the
    /// adapter rather than surviving it as a row nothing can serve.
    /// </summary>
    [Fact]
    public void Switching_docker_off_takes_its_remotes_with_it()
    {
        var remote = new RemoteEngine("abc123", "Build server", RemoteEngineTransport.Ssh, "build-01");

        var built = BackendCatalog.Build(
            includeDemo: false,
            remotes: [remote],
            adapterEnabled: id => id != DockerAdapterModule.BackendId);

        Assert.DoesNotContain(built, p => p.Backend == remote.Backend);
    }

    [Fact]
    public void A_switched_off_plugin_contributes_no_providers()
    {
        BackendCatalog.SetPluginProviders("com.acme.nerdctl", [new StubProvider("nerdctl:default")]);

        Assert.DoesNotContain(
            BackendCatalog.Build(includeDemo: false, adapterEnabled: id => id != "com.acme.nerdctl"),
            p => p.Backend == "nerdctl:default");
    }

    [Fact]
    public void Passing_no_filter_leaves_every_adapter_in()
    {
        BackendCatalog.SetPluginProviders("com.acme.nerdctl", [new StubProvider("nerdctl:default")]);

        var built = BackendCatalog.Build(includeDemo: false);

        Assert.Contains(built, p => p.Backend == DockerAdapterModule.BackendId);
        Assert.Contains(built, p => p.Backend == "nerdctl:default");
    }
}
