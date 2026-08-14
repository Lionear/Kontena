using Kontena.Adapters.Kubernetes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Reading which contexts run a program to log in (KON-365). Like the loopback read, this has to answer
/// from the file alone: the whole point is knowing before anything is started.
/// </summary>
public class KubeconfigExecPluginTests
{
    private static string WriteKubeconfig(string body)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kontena-kubeconfig").FullName, "config");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>
    /// Two contexts on one exec user, a second exec user with arguments, and one plain certificate user.
    /// </summary>
    private const string ConfigYaml = """
        apiVersion: v1
        kind: Config
        current-context: gke-prod
        clusters:
          - name: gke-prod
            cluster:
              server: https://34.90.10.11
          - name: eks-staging
            cluster:
              server: https://api.eks.example
          - name: kind-dev
            cluster:
              server: https://127.0.0.1:6443
        contexts:
          - name: gke-prod
            context:
              cluster: gke-prod
              user: gke-prod
          - name: gke-prod-ro
            context:
              cluster: gke-prod
              user: gke-prod
          - name: eks-staging
            context:
              cluster: eks-staging
              user: eks-staging
          - name: kind-dev
            context:
              cluster: kind-dev
              user: kind-dev
        users:
          - name: gke-prod
            user:
              exec:
                apiVersion: client.authentication.k8s.io/v1beta1
                command: gke-gcloud-auth-plugin
          - name: eks-staging
            user:
              exec:
                apiVersion: client.authentication.k8s.io/v1beta1
                command: aws
                args:
                  - eks
                  - get-token
                  - --cluster-name
                  - staging
          - name: kind-dev
            user:
              client-certificate-data: Zm9v
              client-key-data: YmFy
        """;

    [Fact]
    public void A_context_whose_user_has_an_exec_block_reports_its_command()
    {
        Assert.Equal("gke-gcloud-auth-plugin", ExecCommands()["gke-prod"]);
    }

    [Fact]
    public void The_arguments_are_shown_with_the_command()
    {
        // One line, the way it would be typed: the arguments are where "aws" turns into which cluster
        // and which profile, and a bare command name would hide the interesting half.
        Assert.Equal("aws eks get-token --cluster-name staging", ExecCommands()["eks-staging"]);
    }

    [Fact]
    public void Every_context_on_that_user_is_reported_not_just_the_one_sharing_its_name()
    {
        Assert.Equal("gke-gcloud-auth-plugin", ExecCommands()["gke-prod-ro"]);
    }

    [Fact]
    public void A_context_with_ordinary_credentials_runs_nothing()
    {
        Assert.DoesNotContain("kind-dev", ExecCommands().Keys);
    }

    [Fact]
    public void A_kubeconfig_that_cannot_be_read_reports_nothing_rather_than_throwing()
    {
        // Same contract as LoadContexts: a file with no readable contexts has nothing to offer either.
        Assert.Empty(Kubeconfig.LoadExecCommands("/definitely/not/here/kubeconfig.yaml"));
    }

    [Fact]
    public void A_duplicate_entry_does_not_cost_the_warning()
    {
        // Malformed but readable, and the wrong answer here is the dangerous one: a throw would be caught
        // and reported as "no exec plugin", which is exactly what this file does have.
        var duplicated = ConfigYaml.Replace(
            "  - name: eks-staging\n    user:\n      exec:",
            "  - name: gke-prod\n    user:\n      exec:",
            StringComparison.Ordinal);

        Assert.Contains("gke-prod", Kubeconfig.LoadExecCommands(WriteKubeconfig(duplicated)).Keys);
    }

    private static IReadOnlyDictionary<string, string> ExecCommands() =>
        Kubeconfig.LoadExecCommands(WriteKubeconfig(ConfigYaml));
}
