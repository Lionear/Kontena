using Kontena.Adapters.Kubernetes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Kubeconfigs beyond the default one (KON-118). The part worth testing is the backend id: it ends up in
/// settings and decides which cluster the switcher opens.
/// </summary>
public class KubeconfigSourceTests
{
    [Fact]
    public void The_default_kubeconfig_keeps_the_plain_backend_id()
    {
        // Existing installs have this id in their settings, so it must not change shape.
        var provider = new KubernetesClusterProvider("prod-eu");
        Assert.Equal("kubernetes:prod-eu", provider.Backend);
    }

    [Fact]
    public void Two_files_holding_the_same_context_name_get_different_ids()
    {
        // "default" and "kubernetes-admin@kubernetes" are everywhere. Without the file in the id, adding a
        // second kubeconfig would silently take over the first one's entry.
        var a = new KubernetesClusterProvider("default", "/srv/kubeconfigs/work.yaml");
        var b = new KubernetesClusterProvider("default", "/srv/kubeconfigs/client.yaml");

        Assert.NotEqual(a.Backend, b.Backend);
        Assert.StartsWith("kubernetes@", a.Backend, StringComparison.Ordinal);
        Assert.EndsWith(":default", a.Backend, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_file_always_yields_the_same_id()
    {
        // The id is stored, so it has to survive a restart.
        Assert.Equal(
            new KubernetesClusterProvider("default", "/srv/kubeconfigs/work.yaml").Backend,
            new KubernetesClusterProvider("default", "/srv/kubeconfigs/work.yaml").Backend);
    }

    [Fact]
    public void The_file_path_does_not_appear_in_the_id()
    {
        // Settings are plain text a user may share when reporting a bug; where their files live, and who
        // they are for, is not something to spread around for the sake of a lookup key.
        var provider = new KubernetesClusterProvider("default", "/srv/kubeconfigs/confidential-customer/kubeconfig");
        Assert.DoesNotContain("confidential-customer", provider.Backend, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv", provider.Backend, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_path_is_treated_as_the_default_file()
    {
        Assert.Equal(
            new KubernetesClusterProvider("prod-eu").Backend,
            new KubernetesClusterProvider("prod-eu", "   ").Backend);
    }

    [Fact]
    public void A_kubeconfig_that_cannot_be_read_yields_no_contexts_rather_than_throwing()
    {
        // A config on an unmounted drive should cost its own entries, not the whole switcher.
        Assert.Empty(Kubeconfig.LoadContexts("/definitely/not/here/kubeconfig.yaml"));
    }

    [Fact]
    public void Tilde_is_expanded_because_the_user_types_it_and_the_client_does_not()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, ".kube", "config"), Kubeconfig.Expand("~/.kube/config"));
        Assert.Equal("/etc/kubernetes/admin.conf", Kubeconfig.Expand("/etc/kubernetes/admin.conf"));
    }
}
