using Kontena.Adapters.Kubernetes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Reading which contexts point at an apiserver on this machine (KON-329). It decides one thing —
/// <see cref="KubernetesClusterProvider.ProbeTimeout"/> — and it has to decide it from the file alone,
/// because the whole point is to know before connecting to anything.
/// </summary>
public class KubeconfigLoopbackTests
{
    private static string WriteKubeconfig(string body)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kontena-kubeconfig").FullName, "config");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>
    /// Four contexts on three clusters: kind on loopback, a managed cluster on a hostname, and one on a
    /// private LAN address — that last one is deliberately *not* local, since a 192.168 address can just
    /// as easily be a VPN into a company network.
    /// </summary>
    private const string ConfigYaml = """
        apiVersion: v1
        kind: Config
        current-context: kind-dev
        clusters:
          - name: kind-dev
            cluster:
              server: https://127.0.0.1:6443
          - name: gke-prod
            cluster:
              server: https://34.90.10.11
          - name: lab
            cluster:
              server: https://192.168.1.50:6443
        contexts:
          - name: kind-dev
            context:
              cluster: kind-dev
              user: kind-dev
          - name: gke-prod
            context:
              cluster: gke-prod
              user: gke-prod
          - name: lab
            context:
              cluster: lab
              user: lab
          - name: kind-dev-admin
            context:
              cluster: kind-dev
              user: kind-dev
        users:
          - name: kind-dev
            user: {}
          - name: gke-prod
            user:
              exec:
                apiVersion: client.authentication.k8s.io/v1beta1
                command: gke-gcloud-auth-plugin
          - name: lab
            user: {}
        """;

    [Fact]
    public void Every_context_on_a_loopback_apiserver_is_reported_local()
    {
        var local = LoopbackContexts();

        // Both contexts pointing at the kind cluster, not just the one that shares its name.
        Assert.Contains("kind-dev", local);
        Assert.Contains("kind-dev-admin", local);
    }

    [Fact]
    public void A_cluster_reached_over_the_network_is_not_local()
    {
        var local = LoopbackContexts();

        Assert.DoesNotContain("gke-prod", local);
    }

    [Fact]
    public void A_private_lan_address_is_not_treated_as_local()
    {
        // It may be a VM on this desk or a cluster behind a VPN; those are not the same wait, and the
        // safe side of that guess is the longer deadline.
        Assert.DoesNotContain("lab", LoopbackContexts());
    }

    [Fact]
    public void A_kubeconfig_that_cannot_be_read_reports_nothing_rather_than_throwing()
    {
        // Same contract as LoadContexts: it costs those contexts the longer deadline, nothing else.
        Assert.Empty(Kubeconfig.LoadLoopbackContexts("/definitely/not/here/kubeconfig.yaml"));
    }

    private static IReadOnlySet<string> LoopbackContexts() =>
        Kubeconfig.LoadLoopbackContexts(WriteKubeconfig(ConfigYaml));
}
