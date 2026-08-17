using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The Alerts → "Install with Helm" hand-off (KON-204/207), and the repository it has to bring with
/// it (KON-397): a <c>repo/chart</c> reference resolves to nothing while helm has never heard of the
/// repo, so filling the chart in without it left the user looking the URL up by hand.
/// </summary>
public sealed class MonitoringHelmHandoffTests
{
    [Fact]
    public async Task Installing_monitoring_fills_in_the_chart_and_the_repository_it_lives_in()
    {
        // Where helm is installed the hand-off really runs `helm repo add`, and a test has no
        // business writing to the machine's repository list — so helm is pointed at a throwaway
        // config for the length of this run. Not restored afterwards: nothing in this assembly
        // wants the developer's own repositories, and the add is fired off, so it can still be
        // running when the test returns.
        var scratch = Directory.CreateTempSubdirectory("kontena-helm-handoff");
        Environment.SetEnvironmentVariable("HELM_REPOSITORY_CONFIG", Path.Combine(scratch.FullName, "repositories.yaml"));
        Environment.SetEnvironmentVariable("HELM_REPOSITORY_CACHE", Path.Combine(scratch.FullName, "cache"));

        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute("alerts");
        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);

        Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage).InstallWithHelmCommand.Execute(null);

        var apply = Assert.IsType<ApplyManifestViewModel>(shell.CurrentPage);
        Assert.Equal(ManifestSourceKind.Helm, apply.SourceKind);
        Assert.Equal("prometheus-community/kube-prometheus-stack", apply.Chart);
        Assert.Equal("monitoring", apply.RenderNamespace);

        // The half the ticket is about: the repository the chart reference names, not another one.
        // Whether helm accepted it is helm's business and needs a network; that it is asked for at
        // all is what regressed.
        Assert.Equal("https://prometheus-community.github.io/helm-charts", apply.NewRepoUrl);
        Assert.Equal(apply.Chart.Split('/')[0], apply.NewRepoName);
    }
}
