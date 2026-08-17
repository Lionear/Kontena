using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The alert-detail drawer end to end (KON-208): opening one instance, silencing it, and expiring a
/// silence from the Silenced section's own action — all driven through the shell exactly as a click
/// would, the same shape as <see cref="DetailDrawerTests"/>.
/// </summary>
public sealed class AlertDetailFlowTests
{
    private static async Task<MainWindowViewModel> AlertsShellAsync()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute("alerts");
        await WaitForRowsAsync(shell);

        return shell;
    }

    private static async Task WaitForRowsAsync(MainWindowViewModel shell)
    {
        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);
    }

    private static async Task ConfirmAsync(MainWindowViewModel shell) =>
        await Assert.IsType<ConfirmViewModel>(shell.Dialog).ConfirmCommand.ExecuteAsync(null);

    [Fact]
    public async Task Opening_a_firing_instance_shows_its_own_detail()
    {
        var shell = await AlertsShellAsync();
        var alerts = Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage);

        var group = Assert.Single(alerts.Firing, g => g.Name == "KubePodCrashLooping");
        group.Instances[0].OpenCommand.Execute(null);

        var detail = Assert.IsType<AlertDetailViewModel>(shell.Detail);
        Assert.Equal("KubePodCrashLooping", detail.Name);
        Assert.True(detail.CanCreateSilence);
        Assert.False(detail.CanExpireSilence);

        // The seeded alert carries a namespace/pod, a runbook_url and a generatorURL (KON-205/208) —
        // every footer jump this alert can offer should be offered.
        Assert.True(detail.CanOpenPod);
        Assert.True(detail.CanOpenLogs);
        Assert.True(detail.CanOpenRunbook);
        Assert.True(detail.CanOpenGraph);
    }

    [Fact]
    public async Task Silencing_from_the_drawer_says_what_and_until_when_then_mutes_and_closes()
    {
        var shell = await AlertsShellAsync();
        var alerts = Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage);
        var group = Assert.Single(alerts.Firing, g => g.Name == "KubePodCrashLooping");
        group.Instances[0].OpenCommand.Execute(null);

        Assert.IsType<AlertDetailViewModel>(shell.Detail).SilenceCommand.Execute(null);

        // The confirmation is the whole point (KON-208): it must say what is muted and until when
        // before Alertmanager is ever told.
        var confirm = Assert.IsType<ConfirmViewModel>(shell.Dialog);
        Assert.Contains("KubePodCrashLooping", confirm.Message, StringComparison.Ordinal);
        Assert.True(confirm.HasDetails);
        Assert.Contains(confirm.Details, d => d.Headline == "alertname" && d.Detail == "KubePodCrashLooping");

        await ConfirmAsync(shell);

        // Closed like a Delete would (KON-208): the alert is not gone, but its state changed enough
        // that staying on stale drawer content would be worse than the reload.
        Assert.False(shell.IsDetailOpen);

        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);

        Assert.True(Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage).HasSilenced);
    }

    [Fact]
    public async Task Expiring_from_the_silenced_sections_own_action_unmutes_it()
    {
        var shell = await AlertsShellAsync();
        var alerts = Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage);

        var silenced = Assert.Single(alerts.Silenced, g => g.Name == "KubeJobFailed");
        Assert.True(silenced.CanExpire);
        silenced.ExpireCommand.Execute(null);

        var confirm = Assert.IsType<ConfirmViewModel>(shell.Dialog);
        Assert.Contains("KubeJobFailed", confirm.Message, StringComparison.Ordinal);

        await ConfirmAsync(shell);

        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);

        Assert.False(Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage).HasSilenced);
    }
}
