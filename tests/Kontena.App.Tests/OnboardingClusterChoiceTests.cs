using Kontena.Adapters.Kubernetes;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// What the wizard's cluster ticks are worth after it closes (KON-336): both answers are written, so
/// a cluster that was declined here is not offered again on every launch (KON-120).
/// </summary>
public sealed class OnboardingClusterChoiceTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"kontena-onboard-clusters-{Guid.NewGuid():N}.json");

    private readonly string _kubeconfigPath = Path.Combine(
        Path.GetTempPath(), $"kontena-kubeconfig-{Guid.NewGuid():N}.yaml");

    /// <summary>A kubeconfig of its own, so the test says what is on offer instead of the machine.</summary>
    public OnboardingClusterChoiceTests()
    {
        File.WriteAllText(_kubeconfigPath, """
            apiVersion: v1
            kind: Config
            current-context: alpha
            clusters:
            - name: some-cluster
              cluster:
                server: https://127.0.0.1:6443
            contexts:
            - name: alpha
              context:
                cluster: some-cluster
                user: someone
            - name: beta
              context:
                cluster: some-cluster
                user: someone
            users:
            - name: someone
              user: {}
            """);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _settingsPath, _kubeconfigPath })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private string BackendFor(string context) =>
        new KubernetesClusterProvider(context, _kubeconfigPath).Backend;

    [Fact]
    public async Task Continuing_records_the_ticked_and_the_unticked()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = new KontenaSettings { Onboarded = false, KubeconfigPaths = [_kubeconfigPath] };
        store.Save(settings);

        // No real engine is ever built here (KON-306): this test is about what gets written down, not
        // about reaching a socket.
        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService(),
            buildCatalog: (_, _, _, _) => []);

        await WaitFor(() => vm.IsOnboarding, "the shell never showed the onboarding wizard");

        var alpha = vm.Onboarding!.Clusters.Single(c => c.Backend == BackendFor("alpha"));
        var beta = vm.Onboarding!.Clusters.Single(c => c.Backend == BackendFor("beta"));

        // Both arrive ticked: neither has been seen before.
        Assert.True(alpha.IsSelected);
        Assert.True(beta.IsSelected);

        beta.IsSelected = false;
        vm.Onboarding!.ContinueCommand.Execute(null);

        await WaitFor(() => !vm.IsOnboarding, "continuing never left the wizard");

        var stored = store.Load();
        Assert.True(stored.ShowsCluster(BackendFor("alpha")));
        Assert.False(stored.ShowsCluster(BackendFor("beta")));

        // Declined, not forgotten — the difference between "no" and "not asked yet".
        Assert.Empty(stored.NewClusters([BackendFor("beta")]));
    }

    [Fact]
    public async Task Skipping_decides_nothing_about_the_clusters()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = new KontenaSettings { Onboarded = false, KubeconfigPaths = [_kubeconfigPath] };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService(),
            buildCatalog: (_, _, _, _) => []);

        await WaitFor(() => vm.IsOnboarding, "the shell never showed the onboarding wizard");

        vm.Onboarding!.SkipCommand.Execute(null);

        await WaitFor(() => !vm.IsOnboarding, "skipping never left the wizard");

        // Skip is "not now". Writing a decline for every context would make it "never".
        var stored = store.Load();
        Assert.Empty(stored.KnownClusters);
    }

    private static async Task WaitFor(Func<bool> condition, string complaint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), complaint);
    }
}
