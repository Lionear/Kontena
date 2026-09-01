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
    private readonly List<string> _temporary = [];

    public void Dispose()
    {
        BackendCatalog.ResetPluginProviders();

        foreach (var path in _temporary.Where(File.Exists))
            File.Delete(path);
    }

    /// <summary>
    /// A kubeconfig with one context, so the Kubernetes adapter really contributes a provider. Written
    /// rather than read from this machine: a test whose subject only exists where the developer happens
    /// to have a cluster is one that stops checking anything on CI.
    /// </summary>
    private string WriteKubeconfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kontena-adapter-guard-{Guid.NewGuid():N}.yaml");

        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            current-context: guard-ctx
            clusters:
              - name: guard-cluster
                cluster:
                  server: https://guard.invalid:6443
            users:
              - name: guard-user
                user: {}
            contexts:
              - name: guard-ctx
                context:
                  cluster: guard-cluster
                  user: guard-user
            """);

        _temporary.Add(path);
        return path;
    }

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
    /// Apple's runtime exists only on macOS 26 and up, so it is left out everywhere else rather than
    /// shown switched off — a Windows machine has no decision to make about it. The expectation is
    /// written out here rather than read back from the manifest, so that a manifest that stops saying
    /// "macos 26" fails this instead of agreeing with itself.
    /// </summary>
    [Fact]
    public void An_adapter_that_cannot_run_here_is_not_listed()
    {
        var listed = AdapterCatalog.All([]).Any(a => a.Id == AppleAdapterModule.BackendId);

        Assert.Equal(OperatingSystem.IsMacOSVersionAtLeast(26), listed);
    }

    /// <summary>
    /// The filter is <see cref="PluginPlatform.SupportsHost"/>, and an empty platform list means
    /// "anywhere" — so the manifest declaring the operating system is the whole of what keeps Apple's
    /// runtime off Windows and Linux. Asserted separately from the test above because that one can only
    /// speak for the machine it runs on, and this is the half that would silently go missing (KON-429).
    /// </summary>
    [Fact]
    public void The_Apple_adapter_declares_the_os_it_needs()
    {
        var platform = Assert.Single(AppleAdapterModule.Manifest.Platforms);

        Assert.Equal("macos", platform.Os);
        Assert.Equal("26", platform.MinVersion);
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

    // ── What nothing else checks ────────────────────────────────────────────

    /// <summary>
    /// A bundled adapter's card is described by hand in <see cref="AdapterCatalog.Bundled"/> and its
    /// providers are constructed by hand in <c>BackendCatalog.Build</c>. Nothing holds the two together:
    /// bundled adapters never pass through <c>PluginLoader</c>, so the checks that catch a plugin
    /// misdescribing itself do not apply here at all.
    /// <para>
    /// Both halves of the entry are load-bearing and neither is obvious when wrong. A wrong
    /// <c>Owns</c> silently makes the switch filter the wrong providers — or none — and points
    /// <c>OwnerOf</c> at the wrong adapter, which is what the confirm dialog and the "… is gone"
    /// message are named from. A wrong <c>Contribution</c> mislabels the card forever.
    /// </para>
    /// <para>
    /// The kubeconfig is supplied rather than read off this machine, and every adapter is asserted to
    /// have produced something. Without both, the Kubernetes half is vacuous wherever there is no
    /// kubeconfig — which includes CI — so an <c>Assert.All</c> over an empty list would pass with the
    /// entry plainly wrong. Verified by breaking each half in turn: the emptiness is what hid it.
    /// </para>
    /// </summary>
    [Fact]
    public void Each_bundled_adapter_owns_what_it_produces_and_claims_the_right_kind()
    {
        var kubeconfig = WriteKubeconfig();

        foreach (var adapter in AdapterCatalog.Bundled)
        {
            var mine = BackendCatalog.Build(
                includeDemo: false,
                kubeconfigPaths: [kubeconfig],
                adapterEnabled: id => id == adapter.Id);

            // Vacuity is the failure mode this test exists to avoid, so it is the first thing checked.
            Assert.True(
                mine.Count > 0,
                $"{adapter.Id} produced no providers, so nothing below was actually checked.");

            Assert.All(mine, provider =>
            {
                Assert.True(
                    adapter.Owns(provider.Backend),
                    $"{adapter.Id} produced {provider.Backend} but does not claim it — its switch would "
                    + "not reach that backend, and OwnerOf would name the wrong adapter for it.");

                var expected = provider.Kind == BackendKind.Cluster
                    ? AdapterContribution.Orchestrator
                    : AdapterContribution.ContainerEngine;

                Assert.Equal(expected, adapter.Contribution);
            });
        }
    }

    /// <summary>
    /// Two adapters claiming one backend id makes <see cref="AdapterCatalog.OwnerOf"/> answer with
    /// whichever is listed first — so the confirm dialog and the start-up message would name an adapter
    /// the backend did not come from, and switching off the real one would leave the row behind.
    /// </summary>
    [Fact]
    public void No_two_bundled_adapters_claim_the_same_backend()
    {
        // With a remote in it: Docker claims two families ("docker" and "docker-remote"), which is the
        // one entry here with any room to overlap another.
        var everything = BackendCatalog.Build(
            includeDemo: false,
            remotes: [new RemoteEngine("abc123", "Build server", RemoteEngineTransport.Ssh, "build-01")],
            kubeconfigPaths: []);

        foreach (var provider in everything)
        {
            var claimants = AdapterCatalog.Bundled.Where(a => a.Owns(provider.Backend)).ToList();

            Assert.True(
                claimants.Count <= 1,
                $"{provider.Backend} is claimed by {string.Join(", ", claimants.Select(c => c.Id))}.");
        }
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
