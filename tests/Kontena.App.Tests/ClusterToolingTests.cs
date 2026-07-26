using Kontena.App.ViewModels;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;

namespace Kontena.App.Tests;

/// <summary>Settings › Local clusters (KON-109), driven against a fake tool runner.</summary>
public sealed class ClusterToolingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-page-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ClusterToolingViewModel Subject(FakeToolRunner runner)
        => new(runner, releases: null, store: new ManagedToolStore(_root));

    [Fact]
    public async Task Shows_every_tool_with_its_state()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind, "kind v0.31.0 go1.25.5 linux/amd64")
            .Install(KnownTools.Kubectl, "Client Version: v1.34.9");

        var page = Subject(runner);
        await page.LoadAsync();

        Assert.Equal(3, page.Tools.Count);

        var kind = page.Tools.First(t => t.Name == "kind");
        Assert.True(kind.IsReady);
        Assert.Equal("Detected · v0.31.0", kind.StateText);

        var minikube = page.Tools.First(t => t.Name == "minikube");
        Assert.True(minikube.IsMissing);
        Assert.Equal("Not installed", minikube.StateText);
    }

    [Fact]
    public async Task One_working_tool_is_enough_to_create_a_cluster()
    {
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0"));
        await page.LoadAsync();

        Assert.True(page.CanCreateCluster);
    }

    [Fact]
    public async Task An_outdated_tool_still_counts_as_usable()
    {
        // Warning, not a wall: it is the user's machine, and most of what they want still works.
        var page = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.17.0"));
        await page.LoadAsync();

        var kind = page.Tools.First(t => t.Name == "kind");
        Assert.True(kind.IsOutdated);
        Assert.True(page.CanCreateCluster);
        Assert.Contains("0.20", kind.OutdatedConsequence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_installed_means_no_cluster_can_be_built()
    {
        var page = Subject(new FakeToolRunner());
        await page.LoadAsync();

        Assert.False(page.CanCreateCluster);
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
