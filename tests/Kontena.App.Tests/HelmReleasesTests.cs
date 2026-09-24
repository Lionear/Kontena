using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The Helm releases page and its detail (KON-473): the list, the values/manifest/history reads, and the
/// three writes — each of which has to ask before it runs.
/// </summary>
public sealed class HelmReleasesTests
{
    private readonly FakeClusterEngine _cluster = new();

    private async Task<ClusterHelmReleaseDetailViewModel> DetailAsync(string name = "web")
    {
        var release = (await _cluster.Helm.ListAsync()).Single(r => r.Name == name);
        var detail = new ClusterHelmReleaseDetailViewModel(_cluster.Helm, release);
        await detail.LoadAsync();
        return detail;
    }

    [Fact]
    public async Task The_list_shows_every_release_with_chart_version_and_status()
    {
        using var page = new ClusterHelmReleasesViewModel(_cluster, null);
        await page.LoadAsync();

        var web = page.Items.Single(r => r.Name == "web");
        Assert.Equal("ingress-nginx 4.10.1", web.Chart);
        Assert.Equal("failed", web.Status);
        Assert.False(web.IsHealthy);
        Assert.Equal(2, web.Revision);
        Assert.True(page.Items.Single(r => r.Name == "cache").IsHealthy);
    }

    [Fact]
    public async Task Without_helm_the_page_stays_and_says_so()
    {
        var cluster = new FakeClusterEngine { HasHelm = false };
        using var page = new ClusterHelmReleasesViewModel(cluster, null);
        await page.LoadAsync();

        Assert.True(page.IsHelmMissing);
        Assert.False(page.ShowEmpty);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Uninstall_from_a_row_goes_to_the_shell_and_does_nothing_itself()
    {
        HelmRelease? asked = null;
        using var page = new ClusterHelmReleasesViewModel(_cluster, null, onUninstall: r => asked = r);
        await page.LoadAsync();

        page.Items.Single(r => r.Name == "web").UninstallCommand.Execute(null);

        Assert.Equal("web", asked?.Name);
        Assert.Empty(_cluster.Helm.Writes);
    }

    [Fact]
    public void The_uninstall_confirm_names_the_release_and_says_there_is_no_way_back()
    {
        var (title, message) = HelmWording.Uninstall(new HelmRelease { Name = "web", Namespace = "default" });

        Assert.Equal("Uninstall web?", title);
        Assert.Contains("default", message, StringComparison.Ordinal);
        Assert.Contains("no rollback", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_detail_reads_values_manifest_and_history_newest_first()
    {
        var detail = await DetailAsync();

        Assert.Equal("controller:\n  replicaCount: 3\n", detail.ValuesText);
        Assert.Contains("kind: Deployment", detail.ManifestText, StringComparison.Ordinal);
        Assert.Equal([2, 1], detail.History.Select(h => h.Number));

        // The revision you are on is not one to roll back to.
        Assert.False(detail.History[0].CanRollback);
        Assert.True(detail.History[1].CanRollback);
    }

    [Fact]
    public async Task All_values_include_the_charts_defaults()
    {
        var detail = await DetailAsync();

        detail.ShowAllValues = true;
        await detail.LoadAsync();

        Assert.StartsWith("image:", detail.ValuesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_asks_first_and_runs_only_on_confirm()
    {
        var detail = await DetailAsync();
        ConfirmRequest? asked = null;
        detail.RequestConfirm = r => asked = r;

        detail.History.Single(h => h.Number == 1).RollbackCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.True(asked.Destructive);
        Assert.Equal("Roll back", asked.ConfirmLabel);
        Assert.Empty(_cluster.Helm.Writes);

        await asked.OnConfirm();

        Assert.Equal(["rollback default/web 1"], _cluster.Helm.Writes);
        Assert.Equal(3, detail.Revision);
        Assert.Equal("deployed", detail.Status);
    }

    [Fact]
    public async Task Upgrade_is_only_possible_after_the_values_diff_was_reviewed()
    {
        var detail = await DetailAsync();
        detail.UpgradeChart = "ingress-nginx/ingress-nginx";
        detail.UpgradeValues = "controller:\n  replicaCount: 5\n";

        Assert.False(detail.UpgradeCommand.CanExecute(null));

        detail.ReviewCommand.Execute(null);

        Assert.True(detail.UpgradeCommand.CanExecute(null));
        Assert.Contains(detail.UpgradeDiff, l => l.Text == "-  replicaCount: 3");
        Assert.Contains(detail.UpgradeDiff, l => l.Text == "+  replicaCount: 5");

        // Any edit after the review asks for a new one.
        detail.UpgradeVersion = "4.11.0";
        Assert.False(detail.UpgradeCommand.CanExecute(null));
        Assert.Empty(detail.UpgradeDiff);
    }

    [Fact]
    public async Task Upgrade_asks_first_then_applies_the_reviewed_values()
    {
        var detail = await DetailAsync();
        ConfirmRequest? asked = null;
        detail.RequestConfirm = r => asked = r;
        detail.UpgradeChart = "ingress-nginx/ingress-nginx";
        detail.UpgradeVersion = "4.11.0";
        detail.UpgradeValues = "controller:\n  replicaCount: 5\n";
        detail.ReviewCommand.Execute(null);

        detail.UpgradeCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Empty(_cluster.Helm.Writes);

        await asked.OnConfirm();

        Assert.Equal(["upgrade default/web ingress-nginx/ingress-nginx 4.11.0"], _cluster.Helm.Writes);
        Assert.Equal("ingress-nginx 4.11.0", detail.Chart);
        Assert.Equal("controller:\n  replicaCount: 5\n", detail.ValuesText);
    }

    [Fact]
    public async Task A_failed_read_shows_helms_words_instead_of_an_empty_page()
    {
        var detail = new ClusterHelmReleaseDetailViewModel(new FailingHelm(), new HelmRelease { Name = "web", Namespace = "default" });
        await detail.LoadAsync();

        Assert.Equal(FailingHelm.Down, detail.Error);
    }

    [Fact]
    public async Task A_failed_rollback_throws_helms_words_for_the_confirm_to_show()
    {
        var detail = new ClusterHelmReleaseDetailViewModel(new FailingHelm(), new HelmRelease { Name = "web", Namespace = "default", Revision = 2 });
        ConfirmRequest? asked = null;
        detail.RequestConfirm = r => asked = r;
        detail.ConfirmRollback(new HelmRevision { Revision = 1 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => asked!.OnConfirm());
        Assert.Equal(FailingHelm.Down, ex.Message);
    }

    /// <summary>Every read and write fails the way an unreachable cluster does.</summary>
    private sealed class FailingHelm : Kontena.Sdk.Orchestration.IHelmReleases
    {
        public const string Down = "Error: Kubernetes cluster unreachable";

        public ValueTask<IReadOnlyList<HelmRelease>> ListAsync(string? ns = null, CancellationToken ct = default) => throw new InvalidOperationException(Down);
        public ValueTask<string> GetValuesAsync(string release, string ns, bool all = false, CancellationToken ct = default) => throw new InvalidOperationException(Down);
        public ValueTask<string> GetManifestAsync(string release, string ns, CancellationToken ct = default) => throw new InvalidOperationException(Down);
        public ValueTask<IReadOnlyList<HelmRevision>> GetHistoryAsync(string release, string ns, CancellationToken ct = default) => throw new InvalidOperationException(Down);
        public ValueTask<string?> UpgradeAsync(HelmUpgrade upgrade, CancellationToken ct = default) => ValueTask.FromResult<string?>(Down);
        public ValueTask<string?> RollbackAsync(string release, string ns, int revision, CancellationToken ct = default) => ValueTask.FromResult<string?>(Down);
        public ValueTask<string?> UninstallAsync(string release, string ns, CancellationToken ct = default) => ValueTask.FromResult<string?>(Down);
    }
}
