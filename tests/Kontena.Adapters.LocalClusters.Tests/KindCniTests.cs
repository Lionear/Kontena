using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

/// <summary>
/// The CNI choice a kind cluster can be given (KON-465): what it does to the config, to the command
/// line, and to what is left to apply afterwards.
/// </summary>
public class KindCniTests
{
    private static LocalClusterSpec Spec(string? cni) =>
        new("dev") { Cni = cni, ReadyTimeout = TimeSpan.FromMinutes(5) };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(KindCnis.Kindnet)]
    [InlineData("something-else-entirely")]
    public void Kinds_own_network_changes_nothing(string? cni)
    {
        Assert.False(KindCnis.ReplacesDefault(cni));
        Assert.Null(KindCnis.Manifest(cni));
        Assert.False(KindConfig.Needed(new LocalClusterSpec("dev") { Cni = cni }));

        // The wait is the tool's own business again: kindnet brings the nodes Ready by itself.
        Assert.Contains("--wait", KindArguments.Create(Spec(cni), configPath: null));
    }

    [Theory]
    [InlineData(KindCnis.Calico)]
    [InlineData(KindCnis.Cilium)]
    [InlineData("  CALICO  ")]
    public void A_chosen_network_disables_kindnet_and_is_applied_afterwards(string cni)
    {
        var spec = Spec(cni);

        Assert.True(KindConfig.Needed(spec));
        Assert.Contains("disableDefaultCNI: true", KindConfig.Write(spec), StringComparison.Ordinal);

        // Nothing brings the nodes Ready until the manifest lands, so waiting here would only time out.
        Assert.DoesNotContain("--wait", KindArguments.Create(spec, configPath: "/tmp/kind.yaml"));

        var manifest = Assert.Single(new KindClusterProvisioner(new FakeToolRunner()).PostCreateManifests(spec));
        Assert.NotEmpty(manifest.DisplayName);
    }

    /// <summary>
    /// The whole reason these live in code rather than in a URL template: a version that moves makes two
    /// clusters built a month apart different clusters. If this test is failing you bumped a version —
    /// check the new one boots before you change the expectation.
    /// </summary>
    [Fact]
    public void Both_manifests_are_pinned_to_a_version_rather_than_to_latest()
    {
        foreach (var cni in new[] { KindCnis.Calico, KindCnis.Cilium })
        {
            var manifest = KindCnis.Manifest(cni)!;

            Assert.DoesNotContain("latest", manifest.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("master", manifest.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"\d+\.\d+\.\d+", manifest.Url);
            Assert.StartsWith("https://", manifest.Url, StringComparison.Ordinal);
        }
    }

    /// <summary>Cilium ships no install YAML at all, so its entry has to be rendered from the chart.</summary>
    [Fact]
    public void Cilium_is_a_chart_and_calico_is_a_manifest()
    {
        Assert.Null(KindCnis.Manifest(KindCnis.Calico)!.HelmRelease);

        var cilium = KindCnis.Manifest(KindCnis.Cilium)!;
        Assert.Equal("cilium", cilium.HelmRelease);
        Assert.Equal("kube-system", cilium.Namespace);
        Assert.NotEmpty(cilium.HelmValues);
    }

    [Fact]
    public void The_form_is_offered_kinds_own_first()
    {
        var capabilities = new KindClusterProvisioner(new FakeToolRunner()).Capabilities;

        Assert.True(capabilities.ChoosesCni);
        Assert.Equal(KindCnis.Kindnet, capabilities.Cnis[0]);
        Assert.Equal([KindCnis.Kindnet, KindCnis.Calico, KindCnis.Cilium], capabilities.Cnis);
    }
}
