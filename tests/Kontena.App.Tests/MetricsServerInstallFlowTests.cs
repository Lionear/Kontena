using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Installing a metrics source from the Nodes page (KON-93).
/// <para>
/// This is the action half of the notice KON-68 added. The notice already said the gauges are missing
/// and that metrics-server would fix it; a fresh kind cluster is the common case, not an edge one, and
/// leaving the user to go and read the upstream install docs is where the release's own story stopped.
/// </para>
/// <para>
/// Driven through the fake cluster, which flips its metrics capability when the manifest's APIService
/// is applied — the same thing that makes it real on a cluster. So this exercises the whole flow:
/// confirm, apply, wait, notice gone.
/// </para>
/// </summary>
public sealed class MetricsServerInstallFlowTests
{
    /// <summary>Answers a confirm request the way a user clicking the primary button would.</summary>
    private sealed class Accepts
    {
        public ConfirmRequest? Asked { get; private set; }

        public void Handle(ConfirmRequest request)
        {
            Asked = request;

            // Fire-and-forget, as the real dialog does: the button returns and the work runs on. The
            // tests that care wait for IsInstallingMetrics to fall.
            Work = request.OnConfirm();
        }

        public Task? Work { get; private set; }
    }

    private static async Task<ClusterNodesViewModel> NodesAsync(FakeClusterEngine cluster, Accepts? confirm = null)
    {
        var page = new ClusterNodesViewModel(cluster);
        if (confirm is not null)
            page.RequestConfirm = confirm.Handle;

        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task A_cluster_without_usage_offers_the_install()
    {
        var page = await NodesAsync(new FakeClusterEngine(metrics: false));

        Assert.True(page.ShowMetricsNotice);
        Assert.True(page.CanInstallMetrics);
    }

    [Fact]
    public async Task A_cluster_that_already_has_usage_says_nothing()
    {
        var page = await NodesAsync(new FakeClusterEngine());

        Assert.False(page.ShowMetricsNotice);
    }

    [Fact]
    public async Task Nothing_is_applied_until_the_user_confirms()
    {
        // The install writes to the cluster. A page whose confirm was never wired up must do nothing
        // at all rather than quietly install — the rule ViewModelBase.Confirm exists for.
        var cluster = new FakeClusterEngine(metrics: false);
        var page = await NodesAsync(cluster);

        page.InstallMetricsCommand.Execute(null);

        Assert.False(cluster.Capabilities.Metrics);
        Assert.True(page.ShowMetricsNotice);
    }

    [Fact]
    public async Task The_dialog_names_the_release_the_image_and_what_it_creates()
    {
        // Someone letting this run into their cluster is entitled to know what lands in it, and the
        // list is read off the manifest rather than typed next to it.
        var confirm = new Accepts();
        var page = await NodesAsync(new FakeClusterEngine(metrics: false), confirm);

        page.InstallMetricsCommand.Execute(null);

        var asked = confirm.Asked;
        Assert.NotNull(asked);
        Assert.Contains("metrics-server", asked.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kube-system", asked.Message, StringComparison.Ordinal);
        Assert.Contains(asked.Details!, d => d.Headline.Contains("registry.k8s.io", StringComparison.Ordinal));
        Assert.Contains(asked.Details!, d => d.Detail.Contains("APIService", StringComparison.Ordinal));

        // Not destructive: it adds, and dressing an install in red is how a red dialog stops meaning
        // anything (KON-126).
        Assert.False(asked.Destructive);
    }

    [Fact]
    public async Task Confirming_installs_it_and_the_notice_goes_away()
    {
        var cluster = new FakeClusterEngine(metrics: false);
        var page = await NodesAsync(cluster, new Accepts());

        page.InstallMetricsCommand.Execute(null);
        await WaitUntil(() => !page.IsInstallingMetrics);

        Assert.True(cluster.Capabilities.Metrics);
        Assert.False(page.ShowMetricsNotice);
        Assert.Equal(string.Empty, page.MetricsInstallStatus);
    }

    [Fact]
    public async Task A_kind_context_gets_the_insecure_kubelet_flag_and_the_dialog_says_so()
    {
        // The flag is the difference between a working install and a pod that never becomes ready, so
        // the decision is stated rather than made quietly.
        var confirm = new Accepts();
        var page = await NodesAsync(new FakeClusterEngine("minikube", metrics: false), confirm);

        page.InstallMetricsCommand.Execute(null);

        Assert.NotNull(confirm.Asked);
        Assert.Contains("--kubelet-insecure-tls", confirm.Asked!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_managed_context_is_told_the_certificate_is_expected_to_be_valid()
    {
        var confirm = new Accepts();
        var page = await NodesAsync(new FakeClusterEngine("prod-eu-west", metrics: false), confirm);

        page.InstallMetricsCommand.Execute(null);

        Assert.NotNull(confirm.Asked);
        var message = confirm.Asked!.Message;
        Assert.DoesNotContain("--kubelet-insecure-tls", message, StringComparison.Ordinal);
        Assert.Contains("certificate", message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(done(), "the install did not finish in time");
    }
}
