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
            - name: gamma
              context:
                cluster: some-cluster
                user: someone
            - name: delta
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

    /// <summary>The four contexts the fixture kubeconfig offers, in order.</summary>
    private static readonly string[] Offered = ["alpha", "beta", "gamma", "delta"];

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

    [Fact]
    public async Task Rescanning_keeps_the_clusters_the_user_unticked()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = new KontenaSettings { Onboarded = false, KubeconfigPaths = [_kubeconfigPath] };
        store.Save(settings);

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService(),
            buildCatalog: (_, _, _, _) => []);

        await WaitFor(() => vm.IsOnboarding, "the shell never showed the onboarding wizard");

        var wizard = vm.Onboarding!;
        wizard.Clusters.Single(c => c.Backend == BackendFor("beta")).IsSelected = false;

        // Probing again rebuilds the wizard — the same thing that happens when the wizard starts an
        // engine for you (KON-335). Nothing has been written down yet at this point, so a fresh view
        // model built from stored settings alone sees every context as never-offered.
        await wizard.RescanCommand.ExecuteAsync(null);
        await WaitFor(() => !ReferenceEquals(vm.Onboarding, wizard), "the rescan never rebuilt the wizard");

        Assert.False(
            vm.Onboarding!.Clusters.Single(c => c.Backend == BackendFor("beta")).IsSelected,
            "a rescan re-ticked a cluster the user had unticked");
    }

    [Fact]
    public void Skipping_does_not_become_yes_to_every_cluster_on_the_next_launch()
    {
        // Skip leaves Onboarded = true with no cluster answers, which is exactly the state the one-time
        // adoption at startup treats as "an install that predates the choice" (App.axaml.cs). For an
        // upgraded install that is right; for someone who just said "not now" it answers the question
        // they declined to answer.
        var afterSkip = new KontenaSettings
        {
            Onboarded = true, ClusterChoiceOffered = true, KubeconfigPaths = [_kubeconfigPath],
        };

        string[] discovered = [BackendFor("alpha"), BackendFor("beta")];
        var next = afterSkip.AdoptExistingClusters(discovered).PruneClusters(discovered);

        Assert.False(next.ShowsCluster(BackendFor("alpha")));
        Assert.False(next.ShowsCluster(BackendFor("beta")));

        // Still new, not declined: the next launch may offer them again, which is what "not now" means.
        Assert.Equal(2, next.NewClusters(discovered).Count);
    }

    [Fact]
    public async Task Four_offered_two_unticked_leaves_two_in_the_switcher()
    {
        // The report this ticket came from, end to end (KON-351): four contexts, two unticked, and all
        // four turned up in Kontena afterwards.
        var store = new SettingsStore(_settingsPath);
        var settings = new KontenaSettings { Onboarded = false, KubeconfigPaths = [_kubeconfigPath] };
        store.Save(settings);

        // The catalog's cluster filter is the thing under test, so it is captured rather than stubbed
        // away: this is the predicate the rebuild uses to decide which contexts become providers.
        Func<string, bool>? showsCluster = null;

        var vm = new MainWindowViewModel(
            new BackendRegistry([]), store, settings, new FakeUpdateService(),
            buildCatalog: (_, _, _, shows) => { showsCluster = shows; return []; });

        await WaitFor(() => vm.IsOnboarding, "the shell never showed the onboarding wizard");

        foreach (var context in new[] { "gamma", "delta" })
            vm.Onboarding!.Clusters.Single(c => c.Backend == BackendFor(context)).IsSelected = false;

        vm.Onboarding!.ContinueCommand.Execute(null);
        await WaitFor(() => !vm.IsOnboarding, "continuing never left the wizard");

        var stored = store.Load();
        Assert.True(stored.ShowsCluster(BackendFor("alpha")));
        Assert.True(stored.ShowsCluster(BackendFor("beta")));
        Assert.False(stored.ShowsCluster(BackendFor("gamma")));
        Assert.False(stored.ShowsCluster(BackendFor("delta")));

        Assert.NotNull(showsCluster);
        Assert.Equal(
            [BackendFor("alpha"), BackendFor("beta")],
            Offered.Select(BackendFor).Where(showsCluster).ToArray());
    }

    private static async Task WaitFor(Func<bool> condition, string complaint)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), complaint);
    }
}
