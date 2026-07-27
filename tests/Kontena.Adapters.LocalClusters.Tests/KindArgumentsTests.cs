using Kontena.Core.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Adapters.LocalClusters.Tests;

public class KindArgumentsTests
{
    [Fact]
    public void Plain_create_names_the_cluster_and_asks_for_nothing_else()
    {
        var arguments = KindArguments.Create(new LocalClusterSpec("dev"), configPath: null);

        Assert.Equal(["create", "cluster", "--name", "dev"], arguments);
    }

    [Fact]
    public void Kubernetes_version_becomes_a_node_image()
    {
        var spec = new LocalClusterSpec("dev") { KubernetesVersion = "v1.31.0" };

        var arguments = KindArguments.Create(spec, configPath: null);

        Assert.Contains("--image", arguments);
        Assert.Contains("kindest/node:v1.31.0", arguments);
    }

    [Fact]
    public void A_version_without_its_v_still_produces_a_valid_image_tag()
    {
        var spec = new LocalClusterSpec("dev") { KubernetesVersion = "1.31.0" };

        Assert.Contains("kindest/node:v1.31.0", KindArguments.Create(spec, configPath: null));
    }

    [Fact]
    public void An_explicit_node_image_wins_over_the_version()
    {
        var spec = new LocalClusterSpec("dev")
        {
            KubernetesVersion = "v1.31.0",
            NodeImage = "mirror.internal/kindest/node@sha256:abc",
        };

        var arguments = KindArguments.Create(spec, configPath: null);

        Assert.Contains("mirror.internal/kindest/node@sha256:abc", arguments);
        Assert.DoesNotContain("kindest/node:v1.31.0", arguments);
    }

    [Fact]
    public void A_config_file_is_passed_through()
    {
        var arguments = KindArguments.Create(new LocalClusterSpec("dev"), "/tmp/kind.yaml");

        Assert.Equal(["create", "cluster", "--name", "dev", "--config", "/tmp/kind.yaml"], arguments);
    }

    [Fact]
    public void Ready_timeout_is_expressed_in_whole_seconds()
    {
        var spec = new LocalClusterSpec("dev") { ReadyTimeout = TimeSpan.FromMinutes(5) };

        var arguments = KindArguments.Create(spec, configPath: null);

        Assert.Equal("--wait", arguments[^2]);
        Assert.Equal("300s", arguments[^1]);
    }

    [Fact]
    public void Without_a_timeout_kind_is_not_asked_to_wait()
    {
        Assert.DoesNotContain("--wait", KindArguments.Create(new LocalClusterSpec("dev"), null));
    }

    [Fact]
    public void Delete_and_list_are_the_commands_kind_documents()
    {
        Assert.Equal(["delete", "cluster", "--name", "dev"], KindArguments.Delete("dev"));
        Assert.Equal(["get", "clusters"], KindArguments.List());
    }
}
