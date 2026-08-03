using Kontena.Sdk;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// <see cref="NerdctlPlugin"/> and <see cref="NerdctlEngineProvider"/> are the plugin's front door: one
/// provider per containerd namespace, the <c>moby</c> namespace filtered out because Docker's own
/// containers already appear under the Docker backend, and a guaranteed <c>default</c> fallback when
/// enumeration cannot answer at all — the difference between a plugin the switcher shows as
/// "Not connected" and one that silently contributed nothing.
/// </summary>
public sealed class NerdctlPluginTests
{
    private static readonly string NamespaceLsFixture =
        File.ReadAllText(Path.Combine("Fixtures", "namespace-ls.json"));

    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static FakeToolRunner InstalledWithNamespaces() =>
        Installed().When(_ => true, output: [NamespaceLsFixture]);

    [Fact]
    public void Discovers_one_provider_per_namespace_skipping_moby()
    {
        var providers = NerdctlEngineProvider.DiscoverAll(InstalledWithNamespaces());

        // The fixture holds default, k8s.io and moby (Notes/nerdctl-cli-formats.md) — only the first
        // two should ever become a provider.
        Assert.Equal(["nerdctl:default", "nerdctl:k8s.io"], providers.Select(p => p.Backend));
    }

    [Fact]
    public void Provider_ids_are_unique_and_contain_the_namespace()
    {
        var ids = NerdctlEngineProvider.DiscoverAll(InstalledWithNamespaces()).Select(p => p.Backend).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Contains(':', id));
    }

    [Fact]
    public void Falls_back_to_one_default_provider_when_nerdctl_is_not_installed()
    {
        // No tool installed at all -> NerdctlCli surfaces ToolNotFoundException.
        var providers = NerdctlEngineProvider.DiscoverAll(new FakeToolRunner());

        Assert.Single(providers, p => p.Backend == "nerdctl:default");
    }

    [Fact]
    public void Falls_back_to_one_default_provider_when_the_cli_fails()
    {
        var runner = Installed().When(_ => true, errorOutput: ["nerdctl: command not found"], exitCode: 1);

        var providers = NerdctlEngineProvider.DiscoverAll(runner);

        Assert.Single(providers, p => p.Backend == "nerdctl:default");
    }

    [Fact]
    public void Falls_back_to_one_default_provider_on_empty_output()
    {
        // Installed but unscripted: FakeToolRunner answers with an empty stdout, the same shape a
        // namespace-less nerdctl would print.
        var providers = NerdctlEngineProvider.DiscoverAll(Installed());

        Assert.Single(providers, p => p.Backend == "nerdctl:default");
    }

    [Fact]
    public void DiscoverAll_gives_namespace_ls_a_cancellable_deadline_not_CancellationToken_None()
    {
        // GetProviders() runs synchronously at startup, before there is any window to show progress in.
        // CancellationToken.None never fires, so an unresponsive containerd socket would hold this call
        // (and the whole UI thread behind it) for ToolRunner's ordinary two-minute default before the
        // `default`-namespace fallback ever appeared. Capturing the token handed to RunAsync — rather
        // than waiting out an actual timeout — proves a deadline was attached without making this test
        // slow or flaky.
        var runner = new TokenCapturingToolRunner();

        NerdctlEngineProvider.DiscoverAll(runner);

        Assert.True(runner.CapturedToken.CanBeCanceled);
    }

    private sealed class TokenCapturingToolRunner : IToolRunner
    {
        public CancellationToken CapturedToken { get; private set; }

        public ValueTask<ToolLocation> FindAsync(ExternalTool tool, CancellationToken ct = default) =>
            ValueTask.FromResult(new ToolLocation(tool, "/fake/bin/nerdctl", "v1.0.0"));

        public ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            CapturedToken = ct;
            return ValueTask.FromResult(new ToolResult(0, string.Empty, string.Empty));
        }

        public IAsyncEnumerable<ToolLine> StreamAsync(ToolInvocation invocation, CancellationToken ct = default) =>
            throw new NotSupportedException("DiscoverAll only ever calls RunAsync.");
    }

    [Fact]
    public void DisplayName_is_plain_for_default_and_names_the_namespace_otherwise()
    {
        Assert.Equal("nerdctl", new NerdctlEngineProvider("default", new FakeToolRunner()).DisplayName);
        Assert.Equal("nerdctl (k8s.io)", new NerdctlEngineProvider("k8s.io", new FakeToolRunner()).DisplayName);
    }

    [Fact]
    public void Chip_is_the_letter_badge_with_no_style_override()
    {
        var provider = new NerdctlEngineProvider("default", new FakeToolRunner());

        Assert.Equal("N", provider.Chip);
        // ChipStyle is not declared on the class at all — it stays IBackendProvider's own default, so
        // reading it through the concrete type would not even compile; the interface reference is the point.
        Assert.Null(((IBackendProvider)provider).ChipStyle);
        Assert.Equal(BackendKind.Engine, provider.Kind);
    }

    [Fact]
    public void Manifest_identifies_the_plugin()
    {
        var manifest = new NerdctlPlugin(new FakeToolRunner()).Manifest;

        Assert.Equal("com.kontena.nerdctl", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal("0.4.0", manifest.MinSdkVersion);
    }

    [Fact]
    public void GetProviders_delegates_to_DiscoverAll()
    {
        var plugin = new NerdctlPlugin(InstalledWithNamespaces());

        Assert.Equal(["nerdctl:default", "nerdctl:k8s.io"], plugin.GetProviders().Select(p => p.Backend));
    }
}
