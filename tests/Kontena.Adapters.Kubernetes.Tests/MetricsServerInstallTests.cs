using Kontena.Adapters.Kubernetes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The metrics-server manifest Kontena installs (KON-93).
/// <para>
/// The point of these is provenance and one edit. The manifest is upstream's, embedded, and the
/// constants next to it claim which release — so the first test is that the claim is true. The second
/// group covers the one change Kontena makes to it, which is the change that decides whether the
/// install works at all on kind.
/// </para>
/// </summary>
public class MetricsServerInstallTests
{
    [Fact]
    public void The_embedded_manifest_is_the_release_it_claims_to_be()
    {
        // Published checksum for components.yaml of the pinned release. If this fails, the file and the
        // constants disagree — which is the only way a "pinned" manifest can quietly stop being pinned.
        Assert.Equal(MetricsServerInstall.Sha256, MetricsServerInstall.EmbeddedChecksum());
    }

    [Fact]
    public void The_manifest_runs_the_image_the_constants_name()
    {
        // The dialog names the image. Reading it from a constant while applying a file that runs
        // something else is exactly the kind of half-truth a confirmation must not carry.
        Assert.Contains($"image: {MetricsServerInstall.Image}", MetricsServerInstall.ReadEmbedded(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_flag_the_manifest_is_untouched()
    {
        Assert.Equal(MetricsServerInstall.ReadEmbedded(), MetricsServerInstall.Manifest(insecureKubeletTls: false));
    }

    [Fact]
    public void With_the_flag_it_is_added_once_inside_the_container_args()
    {
        var yaml = MetricsServerInstall.Manifest(insecureKubeletTls: true);

        var lines = yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var flag = lines.FindIndex(l => l.Trim() == "- --kubelet-insecure-tls");

        Assert.True(flag > 0, "the flag was not added");
        Assert.Single(lines, l => l.Trim() == "- --kubelet-insecure-tls");

        // Next to the other args, at the same indentation: one level out is a different key, and YAML
        // would accept it silently.
        var neighbour = lines[flag - 1];
        Assert.Equal("- --metric-resolution=15s", neighbour.Trim());
        Assert.Equal(Indent(neighbour), Indent(lines[flag]));

        // And nothing else moved.
        Assert.Equal(lines.Count - 1, MetricsServerInstall.ReadEmbedded().Split('\n').Length);

        static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
    }

    [Theory]
    [InlineData("kind-kontena")]
    [InlineData("kind")]
    [InlineData("KIND-Dev")]
    [InlineData("minikube")]
    public void The_clusters_kontena_creates_itself_default_to_the_insecure_flag(string context)
    {
        // kind and minikube serve a self-signed kubelet certificate, and they are also the two Kontena
        // provisions (KON-77/78) — so they are the two worth guessing for.
        Assert.True(MetricsServerInstall.LikelyNeedsInsecureKubeletTls(context));
    }

    [Theory]
    [InlineData("gke_prod_europe-west4_main")]
    [InlineData("arn:aws:eks:eu-west-1:1234:cluster/prod")]
    [InlineData("docker-desktop")]
    [InlineData("")]
    [InlineData(null)]
    public void A_managed_cluster_is_left_secure(string? context)
    {
        // The wrong default here is the harmful one: it would tell metrics-server to accept any
        // kubelet certificate on a cluster whose certificates are fine.
        Assert.False(MetricsServerInstall.LikelyNeedsInsecureKubeletTls(context));
    }

    [Fact]
    public void What_it_creates_is_read_off_the_manifest()
    {
        var creates = MetricsServerInstall.Creates();

        // The five kinds that matter to somebody deciding whether to let this run — the APIService is
        // the one that makes metrics.k8s.io answer at all.
        Assert.Contains("Deployment", creates);
        Assert.Contains("APIService", creates);
        Assert.Contains("ServiceAccount", creates);
        Assert.Contains("Service", creates);
        Assert.Contains(creates, c => c.StartsWith("ClusterRole", StringComparison.Ordinal));

        // Counted where there is more than one, because "ClusterRole" twice in a list reads as a bug.
        Assert.Contains("ClusterRole ×2", creates);
    }
}
