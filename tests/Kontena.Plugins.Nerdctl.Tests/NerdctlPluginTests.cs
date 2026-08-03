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
        //
        // This only pins "not None" — a 1-tick CancellationTokenSource would satisfy CanBeCanceled just
        // as well. It does not pin the five-second duration, and on its own it does not prove that an
        // expired deadline actually reaches the `default`-provider fallback — the next test does that
        // part, by simulating the token already having fired rather than waiting the real five seconds.
        var runner = new TokenCapturingToolRunner();

        NerdctlEngineProvider.DiscoverAll(runner);

        Assert.True(runner.CapturedToken.CanBeCanceled);
    }

    [Fact]
    public void DiscoverAll_falls_back_to_default_when_the_deadline_fires()
    {
        // Simulates the five-second deadline having already elapsed by the time nerdctl would have
        // answered — the same OperationCanceledException(ct) a real cancelled RunAsync raises — without
        // making the test wait out the actual five seconds. If DiscoverAll ever went back to
        // CancellationToken.None, this token would never be cancellable, RunAsync would return
        // CanceledToolRunner's default fallback instead of throwing, the "catch (Exception)" below would
        // never fire, and DiscoverAll would return an empty list rather than one default provider —
        // failing this test the same way the `EngineProvider_ids_...` cases above already fail similar
        // regressions.
        var runner = new CanceledToolRunner();

        var providers = NerdctlEngineProvider.DiscoverAll(runner);

        Assert.Single(providers, p => p.Backend == "nerdctl:default");
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

    /// <summary>A namespace list that never answers — it only ever reports the deadline that was
    /// supposed to end it, exactly as a real cancelled <c>RunAsync</c> would once its token fires.</summary>
    private sealed class CanceledToolRunner : IToolRunner
    {
        public ValueTask<ToolLocation> FindAsync(ExternalTool tool, CancellationToken ct = default) =>
            ValueTask.FromResult(new ToolLocation(tool, "/fake/bin/nerdctl", "v1.0.0"));

        public ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            if (!ct.CanBeCanceled)
                throw new InvalidOperationException("DiscoverAll must hand RunAsync a cancellable token.");

            throw new OperationCanceledException("Simulated: the deadline elapsed before nerdctl answered.", ct);
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
