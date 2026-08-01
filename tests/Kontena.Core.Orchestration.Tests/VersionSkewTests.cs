using Kontena.Sdk.Orchestration;
using Xunit;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The skew policy is pure comparison, so the tests are the specification: what the published
/// Kubernetes version skew policy allows, and what Kontena must therefore flag (KON-95).
/// </summary>
public class VersionSkewTests
{
    [Theory]
    [InlineData("v1.29.4", 1, 29)]
    [InlineData("1.29.4", 1, 29)]
    [InlineData("V1.29.4", 1, 29)]
    [InlineData("v1.29", 1, 29)]
    [InlineData("v1.29.4-gke.1043000", 1, 29)]
    [InlineData("v1.30.0+k3s1", 1, 30)]
    [InlineData("v1.30+k3s1", 1, 30)]
    [InlineData("  v1.31.2  ", 1, 31)]
    public void A_version_reduces_to_major_and_minor(string text, int major, int minor)
    {
        Assert.Equal(new KubernetesVersion(major, minor), KubernetesVersion.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("v1")]
    [InlineData("v1.")]
    [InlineData("unknown")]
    [InlineData("vX.Y.Z")]
    [InlineData("-1.29.0")]
    public void An_unreadable_version_is_null_rather_than_a_guess(string? text)
    {
        Assert.Null(KubernetesVersion.Parse(text));
    }

    [Fact]
    public void A_kubelet_matching_the_apiserver_is_supported()
    {
        var skew = VersionSkewPolicy.Evaluate("v1.30.2", "v1.30.0");

        Assert.Equal(VersionSkewState.Supported, skew.State);
        Assert.Equal(0, skew.MinorsBehind);
        Assert.False(skew.IsProblem);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_kubelet_within_three_minors_of_a_modern_apiserver_is_supported(int behind)
    {
        var skew = VersionSkewPolicy.Evaluate("v1.31.0", $"v1.{31 - behind}.0");

        Assert.Equal(VersionSkewState.Supported, skew.State);
        Assert.Equal(behind, skew.MinorsBehind);
    }

    [Fact]
    public void A_kubelet_four_minors_behind_is_outside_the_window()
    {
        var skew = VersionSkewPolicy.Evaluate("v1.31.0", "v1.27.5");

        Assert.Equal(VersionSkewState.Outdated, skew.State);
        Assert.Equal(4, skew.MinorsBehind);
        Assert.True(skew.IsProblem);
        Assert.Contains("4 minor versions", skew.Summary);
    }

    [Fact]
    public void Before_1_28_the_window_was_two_minors_not_three()
    {
        // 1.28 widened the allowed lag from 2 to 3; an older control plane still only allows 2, and
        // saying otherwise would clear a node that the cluster itself does not support.
        Assert.Equal(2, VersionSkewPolicy.SupportedMinorLag(new KubernetesVersion(1, 27)));
        Assert.Equal(3, VersionSkewPolicy.SupportedMinorLag(new KubernetesVersion(1, 28)));

        Assert.Equal(VersionSkewState.Supported, VersionSkewPolicy.Evaluate("v1.27.0", "v1.25.0").State);
        Assert.Equal(VersionSkewState.Outdated, VersionSkewPolicy.Evaluate("v1.27.0", "v1.24.0").State);
    }

    [Fact]
    public void A_kubelet_newer_than_the_apiserver_is_an_error_even_by_one_minor()
    {
        var skew = VersionSkewPolicy.Evaluate("v1.30.0", "v1.31.0");

        Assert.Equal(VersionSkewState.Ahead, skew.State);
        Assert.Equal(-1, skew.MinorsBehind);
        Assert.True(skew.IsProblem);
    }

    [Fact]
    public void A_kubelet_newer_only_in_patch_is_still_supported()
    {
        // The policy is expressed in minors; a node one patch ahead is normal during a rollout.
        var skew = VersionSkewPolicy.Evaluate("v1.30.0", "v1.30.4");

        Assert.Equal(VersionSkewState.Supported, skew.State);
    }

    [Fact]
    public void A_whole_major_behind_is_outside_any_window()
    {
        var skew = VersionSkewPolicy.Evaluate("v2.1.0", "v1.31.0");

        Assert.Equal(VersionSkewState.Outdated, skew.State);
        Assert.True(skew.IsProblem);
    }

    [Theory]
    [InlineData(null, "v1.30.0")]
    [InlineData("v1.30.0", null)]
    [InlineData("", "")]
    [InlineData("unknown", "v1.30.0")]
    public void An_unreadable_version_reports_unknown_rather_than_a_problem(string? apiServer, string? kubelet)
    {
        var skew = VersionSkewPolicy.Evaluate(apiServer, kubelet);

        Assert.Equal(VersionSkewState.Unknown, skew.State);
        Assert.False(skew.IsProblem);
    }

    [Fact]
    public void The_detail_names_both_versions_so_the_chip_can_be_acted_on()
    {
        var skew = VersionSkewPolicy.Evaluate("v1.31.0", "v1.26.0");

        Assert.Contains("v1.26", skew.Detail);
        Assert.Contains("v1.31", skew.Detail);
    }
}
