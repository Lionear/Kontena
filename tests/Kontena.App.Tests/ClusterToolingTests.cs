using Kontena.App.ViewModels;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.App.Tests;

/// <summary>Settings › Tools (KON-109, moved by KON-266), driven against a fake tool runner.</summary>
public sealed class ClusterToolingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-page-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A fake release source, never null: the update check runs in the background on every load, and a
    /// null source would fall back to the real one and put a network call in every one of these tests.
    /// </summary>
    private ClusterToolingViewModel Subject(FakeToolRunner runner, FakeToolReleaseSource? releases = null)
        => new(runner, releases ?? new FakeToolReleaseSource(), new ManagedToolStore(_root));

    [Fact]
    public async Task Shows_every_tool_with_its_state()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0 go1.25.5 linux/amd64")
            .Install(KnownTools.Kubectl, "Client Version: v1.34.9");

        var page = Subject(runner);
        await page.LoadAsync();

        Assert.Equal(KnownTools.All.Count, page.Tools.Count());

        var kind = page.Tools.First(t => t.Name == "kind");
        Assert.True(kind.IsReady);
        Assert.Equal("Detected · v0.31.0", kind.StateText);

        var minikube = page.Tools.First(t => t.Name == "minikube");
        Assert.True(minikube.IsMissing);
        Assert.Equal("Not installed", minikube.StateText);
    }

    /// <summary>
    /// KON-266: the tools needed for every cluster are not filed under the machine that may never
    /// build one. The heading is what carries that, so the heading is what is asserted.
    /// </summary>
    [Fact]
    public async Task Kubectl_and_helm_are_not_local_cluster_tooling()
    {
        var page = Subject(new FakeToolRunner());
        await page.LoadAsync();

        var everyCluster = page.Groups.First(g => g.Title == "Working with clusters");
        Assert.Equal(["kubectl", "helm", "kustomize"], everyCluster.Tools.Select(t => t.Name));

        var thisMachine = page.Groups.First(g => g.Title == "Clusters on this machine");
        Assert.Equal(["kind", "minikube"], thisMachine.Tools.Select(t => t.Name));
    }

    /// <summary>
    /// The trap KON-266 names: the purpose used to come from a dictionary this page kept, keyed by
    /// executable and read through an indexer, so a seventh tool would have thrown rather than shown a
    /// blank line. It comes off the tool now, and this is what says every tool brought one.
    /// </summary>
    [Fact]
    public async Task Every_tool_says_what_it_is_for()
    {
        var page = Subject(new FakeToolRunner());
        await page.LoadAsync();

        Assert.All(page.Tools, t => Assert.NotEmpty(t.Purpose));
    }

    [Fact]
    public async Task An_outdated_tool_is_a_warning_rather_than_a_wall()
    {
        // It is the user's machine, and most of what they want still works.
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.17.0"));
        await page.LoadAsync();

        var kind = page.Tools.First(t => t.Name == "kind");
        Assert.True(kind.IsOutdated);
        Assert.Contains("0.20", kind.OutdatedConsequence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_kubectl_can_be_fetched_like_kind_and_minikube()
    {
        // kubectl is published on dl.k8s.io rather than GitHub, which is the only reason its row used
        // to be the one without buttons (KON-256).
        var page = Subject(new FakeToolRunner());
        await page.LoadAsync();

        var kubectl = page.Tools.First(t => t.Name == "kubectl");

        Assert.True(kubectl.IsMissing);
        Assert.Equal(ToolPlatform.CanDownload, kubectl.CanDownload);
    }

    [Fact]
    public async Task A_kubectl_that_is_already_installed_is_not_offered_as_a_download()
    {
        // A system install wins; what is on offer is handing it over, not fetching a second copy.
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kubectl, "Client Version: v1.34.9"));
        await page.LoadAsync();

        var kubectl = page.Tools.First(t => t.Name == "kubectl");

        Assert.True(kubectl.IsReady);
        Assert.False(kubectl.CanDownload);
        Assert.Equal(ToolPlatform.CanDownload, kubectl.CanHandOver);
    }

    [Fact]
    public async Task An_outdated_kubectl_says_what_that_costs_in_its_own_terms()
    {
        // Not "the cluster settings it writes" — Kontena never builds a cluster with kubectl.
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kubectl, "Client Version: v1.26.0"));
        await page.LoadAsync();

        var kubectl = page.Tools.First(t => t.Name == "kubectl");

        Assert.True(kubectl.IsOutdated);
        Assert.Contains("kustomize", kubectl.OutdatedConsequence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_installed_shows_every_tool_as_missing()
    {
        var page = Subject(new FakeToolRunner());
        await page.LoadAsync();

        Assert.All(page.Tools, t => Assert.True(t.IsMissing));
    }

    [Fact]
    public async Task Installing_streams_the_package_managers_own_output()
    {
        var runner = new FakeToolRunner()
            .Install(new ExternalTool("brew", "brew", ["--version"], []))
            .When(i => i.Arguments.Contains("kind"), output: ["==> Fetching kind", "==> Pouring kind"]);

        var page = Subject(runner);
        await page.LoadAsync();

        var hint = new InstallHint(PackageManager.Homebrew, "brew", ["install", "kind"]);
        await page.InstallAsync(page.Tools.First(t => t.Name == "kind"), hint);

        Assert.Contains("==> Fetching kind", page.Output);
        Assert.Null(page.Error);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task A_failing_install_reports_the_tool_s_own_words()
    {
        var runner = new FakeToolRunner()
            .Install(new ExternalTool("brew", "brew", ["--version"], []))
            .When(_ => true, exitCode: 1, errorOutput: ["Error: No available formula with the name \"kynd\""]);

        var page = Subject(runner);
        await page.LoadAsync();

        await page.InstallAsync(
            page.Tools.First(t => t.Name == "kind"),
            new InstallHint(PackageManager.Homebrew, "brew", ["install", "kynd"]));

        Assert.NotNull(page.Error);
        Assert.Contains("No available formula", page.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_with_no_managed_copy_cannot_be_removed()
    {
        // The button is hidden, and the command behind it is gated too — a hidden control that still
        // fires is one keyboard shortcut away from being a visible one.
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0"));
        await page.LoadAsync();

        var row = page.Tools.First(t => t.Name == "kind");

        ConfirmRequest? asked = null;
        page.RequestConfirm = request => asked = request;
        row.RemoveCommand.Execute(null);

        Assert.False(row.CanRemove);
        Assert.False(row.RemoveCommand.CanExecute(null));
        Assert.Null(asked);
    }

    [Fact]
    public async Task Re_checking_updates_the_rows_in_place()
    {
        var runner = new FakeToolRunner();
        var page = Subject(runner);
        await page.LoadAsync();

        var row = page.Tools.First(t => t.Name == "kind");
        Assert.True(row.IsMissing);

        // Someone installed it in a terminal while the page was open.
        runner.Install(KnownTools.Kind, "kind v0.31.0");
        await page.LoadAsync();

        Assert.Same(row, page.Tools.First(t => t.Name == "kind"));
        Assert.True(row.IsReady);
    }
}
