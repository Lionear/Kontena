using Kontena.Adapters.Kubernetes;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// What a cluster gets to answer a probe in (KON-329, after KON-327 gave remotes their own budget).
/// <para>
/// A kube-context crosses a network before it can answer — a managed cluster sits in a region, and its
/// kubeconfig often starts an <c>exec:</c> credential plugin before the first call — so the two seconds
/// a local socket gets marks it "Not connected" no matter how healthy it is. A cluster whose apiserver
/// is on this machine is the opposite case: it answers in milliseconds or is not running, and since a
/// probe round runs its members together and costs whatever the slowest one costs, giving that one the
/// long deadline would make every round wait on a stopped <c>kind</c> cluster.
/// </para>
/// <para>
/// Both assertions compare against <see cref="IBackendProvider"/>'s own default rather than a literal
/// number, so what is pinned is the relationship — raising the interface default cannot silently make
/// these tests meaningless.
/// </para>
/// </summary>
public class ClusterProbeBudgetTests
{
    /// <summary>Stands in for Docker or Podman on this machine: it takes the interface default.</summary>
    private sealed class LocalProvider : IBackendProvider
    {
        public string Backend => "local";
        public string DisplayName => "Local";
        public string Chip => "L";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => throw new NotSupportedException("never created here");
    }

    /// <summary>Read through the interface: a default interface member is not reachable off the class.</summary>
    private static TimeSpan LocalSocketBudget
    {
        get
        {
            IBackendProvider local = new LocalProvider();
            return local.ProbeTimeout;
        }
    }

    [Fact]
    public void A_cluster_off_this_machine_gets_more_than_a_local_socket()
    {
        var cluster = new KubernetesClusterProvider("prod-eu-west");

        Assert.True(
            cluster.ProbeTimeout > LocalSocketBudget,
            "a managed cluster crosses a WAN, and often starts an auth plugin, before it can answer");
    }

    [Fact]
    public void A_cluster_on_this_machine_keeps_the_local_budget()
    {
        var kind = new KubernetesClusterProvider("kind-dev", null, loopbackServer: true);

        Assert.Equal(LocalSocketBudget, kind.ProbeTimeout);
    }

    [Fact]
    public void A_context_nobody_said_anything_about_errs_towards_the_longer_deadline()
    {
        // The wizard builds providers from a bare context name to read their id. Defaulting those to the
        // short budget would be the KON-327 failure again: a reachable cluster declared dead. The cost of
        // the other direction is a slower probe round, which is the cheaper mistake.
        var bare = new KubernetesClusterProvider("whatever");

        Assert.True(bare.ProbeTimeout > LocalSocketBudget);
    }
}
