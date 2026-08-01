using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;
using Kontena.Core.Orchestration;

namespace Kontena.Adapters.LocalClusters.Tests;

/// <summary>
/// Reading <c>minikube profile list -o json</c>. The samples here are minikube's own output, taken
/// from the tool rather than from its documentation (KON-142); the point of most of these tests is
/// what happens when it is <i>not</i> that shape, because that is the case a version bump produces
/// and an empty cluster list is an expensive way to find out.
/// </summary>
public class MinikubeProfilesTests
{
    private const string Sample = """
        {
          "invalid": [],
          "valid": [
            {
              "Name": "dev",
              "Status": "OK",
              "Config": {
                "Name": "dev",
                "Driver": "docker",
                "Nodes": [ { "Name": "" }, { "Name": "dev-m02" } ]
              }
            },
            {
              "Name": "old",
              "Status": "Stopped",
              "Config": { "Name": "old", "Driver": "kvm2", "Nodes": [ { "Name": "" } ] }
            }
          ]
        }
        """;

    [Fact]
    public void Profiles_become_clusters_with_their_state_and_driver()
    {
        var clusters = MinikubeProfiles.Parse(Sample, "minikube");

        Assert.Equal(2, clusters.Count);

        var dev = clusters[0];
        Assert.Equal("dev", dev.Name);
        Assert.Equal("minikube", dev.Provisioner);
        Assert.Equal(LocalClusterState.Running, dev.State);
        Assert.Equal("docker", dev.Driver);
        Assert.Equal(2, dev.Nodes.Count);

        Assert.Equal(LocalClusterState.Stopped, clusters[1].State);
        Assert.Equal("kvm2", clusters[1].Driver);
    }

    [Fact]
    public void The_context_is_the_profile_name()
    {
        // minikube names the kubeconfig context after the profile, unlike kind's prefixed form. This is
        // the join to the switcher, so it is worth pinning.
        Assert.Equal("dev", MinikubeProfiles.Parse(Sample, "minikube")[0].Context);
    }

    [Fact]
    public void A_nameless_node_still_counts()
    {
        // minikube leaves the first node's name empty in its own output.
        Assert.All(MinikubeProfiles.Parse(Sample, "minikube")[0].Nodes, n => Assert.NotEmpty(n));
    }

    [Fact]
    public void A_running_profile_says_OK_rather_than_Running()
    {
        // Verbatim from minikube v1.38.1, trimmed to the fields that are read. "OK" is the rollup over
        // the profile's components; "Running" only appears in the per-node output, which this command
        // does not print. Reading "OK" as unknown cost every running cluster its Stop button (KON-142).
        const string json = """
            {
              "invalid": [],
              "valid": [
                {
                  "Name": "test",
                  "Status": "OK",
                  "Config": {
                    "Name": "test",
                    "Driver": "docker",
                    "KubernetesConfig": { "KubernetesVersion": "v1.35.1", "ClusterName": "test" },
                    "Nodes": [ { "Name": "", "IP": "192.168.49.2", "ControlPlane": true } ]
                  },
                  "Active": false,
                  "ActiveKubeContext": true
                }
              ]
            }
            """;

        var cluster = Assert.Single(MinikubeProfiles.Parse(json, "minikube"));

        Assert.Equal(LocalClusterState.Running, cluster.State);
        Assert.Equal("docker", cluster.Driver);
        Assert.Single(cluster.Nodes);
    }

    [Fact]
    public void Running_is_taken_as_running_too()
    {
        // Kept alongside "OK": which word a version prints is not something we control, and the cost of
        // accepting both is nothing.
        const string json = """{"valid":[{"Name":"dev","Status":"Running","Config":{"Name":"dev"}}]}""";

        Assert.Equal(LocalClusterState.Running, MinikubeProfiles.Parse(json, "minikube")[0].State);
    }

    [Fact]
    public void A_status_we_do_not_know_is_unknown_rather_than_guessed()
    {
        const string json = """
            {"valid":[{"Name":"dev","Status":"Pending","Config":{"Name":"dev"}}]}
            """;

        Assert.Equal(LocalClusterState.Unknown, MinikubeProfiles.Parse(json, "minikube")[0].State);
    }

    [Fact]
    public void A_profile_with_nothing_but_a_name_is_still_a_cluster()
    {
        const string json = """{"valid":[{"Name":"dev"}]}""";

        var cluster = Assert.Single(MinikubeProfiles.Parse(json, "minikube"));

        Assert.Equal("dev", cluster.Name);
        Assert.Equal(LocalClusterState.Unknown, cluster.State);
        Assert.Null(cluster.Driver);
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public void A_name_that_only_lives_in_the_config_is_found_too()
    {
        const string json = """{"valid":[{"Status":"Running","Config":{"Name":"dev"}}]}""";

        Assert.Equal("dev", Assert.Single(MinikubeProfiles.Parse(json, "minikube")).Name);
    }

    [Fact]
    public void Invalid_profiles_are_left_out()
    {
        const string json = """
            {"invalid":[{"Name":"broken"}],"valid":[{"Name":"dev"}]}
            """;

        Assert.Equal(["dev"], MinikubeProfiles.Parse(json, "minikube").Select(c => c.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"valid": null}""")]
    [InlineData("""{"valid": "surprise"}""")]
    public void Anything_that_is_not_the_expected_shape_yields_nothing(string json)
    {
        Assert.Empty(MinikubeProfiles.Parse(json, "minikube"));
    }
}
