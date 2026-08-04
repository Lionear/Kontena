using Kontena.Adapters.Kubernetes;
using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// The wizard offers the clusters in the kubeconfig, not only the engines on the machine (KON-336).
/// <para>
/// Someone with a kubeconfig and no local engine used to be told "no engines detected" and handed the
/// Podman install guide — sent to install software they do not need, past clusters that were ready.
/// </para>
/// </summary>
public sealed class OnboardingClusterTests
{
    private static OnboardingViewModel Wizard(params string[] contexts) => new(
        probes: [],
        fakeBackend: "fake",
        autoDetect: true,
        onContinue: _ => { },
        onSkip: () => { },
        onInstallPodman: () => { },
        onRescan: () => Task.CompletedTask,
        onStartEngine: () => Task.CompletedTask,
        showRoadmap: false,
        clusters: [.. contexts.Select(c => new KubernetesClusterProvider(c))]);

    [Fact]
    public void Clusters_alone_are_enough_to_continue()
    {
        var wizard = Wizard("alpha", "beta");

        Assert.True(wizard.HasClusters);
        Assert.False(wizard.HasConnectedEngine);
        Assert.True(wizard.CanContinue);
    }

    [Fact]
    public void Install_guide_steps_aside_for_a_machine_that_has_clusters()
    {
        Assert.False(Wizard("alpha").ShowInstallAssist);
        Assert.True(Wizard().ShowInstallAssist);
    }

    [Fact]
    public void Headline_names_what_this_machine_actually_has()
    {
        Assert.Contains("clusters", Wizard("alpha").Headline, StringComparison.Ordinal);
        Assert.Contains("engine", Wizard().Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Unticking_every_cluster_leaves_nothing_to_continue_with()
    {
        var wizard = Wizard("alpha");

        wizard.Clusters[0].IsSelected = false;

        Assert.Equal(0, wizard.SelectedClusterCount);
        Assert.False(wizard.CanContinue);
    }

    [Fact]
    public void Continue_lands_on_the_first_ticked_cluster()
    {
        string? landed = null;
        var wizard = new OnboardingViewModel(
            probes: [],
            fakeBackend: "fake",
            autoDetect: true,
            onContinue: backend => landed = backend,
            onSkip: () => { },
            onInstallPodman: () => { },
            onRescan: () => Task.CompletedTask,
            onStartEngine: () => Task.CompletedTask,
            showRoadmap: false,
            clusters: [new KubernetesClusterProvider("alpha"), new KubernetesClusterProvider("beta")]);

        wizard.Clusters[0].IsSelected = false;
        wizard.ContinueCommand.Execute(null);

        Assert.Equal(new KubernetesClusterProvider("beta").Backend, landed);
    }

    /// <summary>A cluster already declined comes back unticked rather than hidden — changing your mind
    /// stays possible, being nagged does not (KON-120).</summary>
    [Fact]
    public void A_declined_cluster_comes_back_unticked()
    {
        var wizard = new OnboardingViewModel(
            probes: [],
            fakeBackend: "fake",
            autoDetect: true,
            onContinue: _ => { },
            onSkip: () => { },
            onInstallPodman: () => { },
            onRescan: () => Task.CompletedTask,
            onStartEngine: () => Task.CompletedTask,
            showRoadmap: false,
            clusters: [new KubernetesClusterProvider("alpha"), new KubernetesClusterProvider("beta")],
            clusterTicked: id => id.EndsWith("alpha", StringComparison.Ordinal));

        Assert.True(wizard.Clusters[0].IsSelected);
        Assert.False(wizard.Clusters[1].IsSelected);
    }
}
