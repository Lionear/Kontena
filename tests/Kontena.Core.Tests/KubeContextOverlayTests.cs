using Kontena.Core.Shell;

namespace Kontena.Core.Tests;

/// <summary>
/// The kubeconfig overlay that points a terminal at one cluster without touching the user's own file
/// (KON-171).
/// </summary>
public sealed class KubeContextOverlayTests
{
    [Fact]
    public void The_overlay_sets_the_current_context()
    {
        var yaml = KubeContextOverlay.Compose("kind-test", cluster: null, user: null, @namespace: null);

        Assert.Contains("current-context: \"kind-test\"", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap this file exists to avoid. <c>KUBECONFIG</c> merges list-valued keys by name and the
    /// first file wins, so a <c>contexts</c> entry naming no cluster and no user does not add a
    /// namespace to the real context — it <em>replaces</em> it with one that points nowhere, and every
    /// command in that shell fails. Without both names, the namespace is simply not pinned.
    /// </summary>
    [Theory]
    [InlineData(null, "rick")]
    [InlineData("kind-test", null)]
    [InlineData(null, null)]
    public void A_namespace_is_not_pinned_when_it_would_shadow_the_real_context(string? cluster, string? user)
    {
        var yaml = KubeContextOverlay.Compose("kind-test", cluster, user, @namespace: "argocd");

        Assert.DoesNotContain("contexts:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("argocd", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_namespace_is_pinned_by_restating_the_context_it_belongs_to()
    {
        var yaml = KubeContextOverlay.Compose("kind-test", "kind-test", "kind-test", "argocd");

        Assert.Contains("contexts:", yaml, StringComparison.Ordinal);
        Assert.Contains("namespace: \"argocd\"", yaml, StringComparison.Ordinal);
        Assert.Contains("cluster: \"kind-test\"", yaml, StringComparison.Ordinal);
        Assert.Contains("user: \"kind-test\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_context_without_a_namespace_writes_no_contexts_block()
    {
        var yaml = KubeContextOverlay.Compose("kind-test", "kind-test", "kind-test", @namespace: null);

        Assert.DoesNotContain("contexts:", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// EKS contexts are ARNs, and a bare colon opens a mapping in YAML. Quoting is not cosmetic here:
    /// unquoted, the overlay parses as something else entirely.
    /// </summary>
    [Fact]
    public void Context_names_with_colons_stay_one_value()
    {
        var yaml = KubeContextOverlay.Compose(
            "arn:aws:eks:eu-west-1:123456789012:cluster/prod", null, null, null);

        Assert.Contains(
            "current-context: \"arn:aws:eks:eu-west-1:123456789012:cluster/prod\"",
            yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_and_backslashes_in_a_name_are_escaped()
    {
        var yaml = KubeContextOverlay.Compose("we\"ird\\one", null, null, null);

        Assert.Contains("current-context: \"we\\\"ird\\\\one\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_goes_in_front_of_the_files_that_were_already_there()
    {
        var value = KubeContextOverlay.ComposeKubeconfigValue(
            "/tmp/session/kubeconfig.yaml", ["/home/rick/.kube/config", "/home/rick/work.yaml"]);

        Assert.Equal(
            string.Join(Path.PathSeparator,
                "/tmp/session/kubeconfig.yaml", "/home/rick/.kube/config", "/home/rick/work.yaml"),
            value);
    }

    /// <summary>
    /// The same file can arrive twice — the cluster's own kubeconfig is often the default one as well.
    /// Listing it twice is harmless to kubectl and confusing to read, and the second copy is the one
    /// that would quietly change meaning if the first were ever removed.
    /// </summary>
    [Fact]
    public void Repeated_and_empty_paths_are_dropped()
    {
        var value = KubeContextOverlay.ComposeKubeconfigValue(
            "/tmp/overlay.yaml", ["/home/rick/.kube/config", "  ", "/home/rick/.kube/config", ""]);

        Assert.Equal(
            string.Join(Path.PathSeparator, "/tmp/overlay.yaml", "/home/rick/.kube/config"),
            value);
    }
}
