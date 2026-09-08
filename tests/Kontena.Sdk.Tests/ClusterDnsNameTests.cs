using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Sdk.Tests;

public sealed class ClusterDnsNameTests
{
    [Fact]
    public void Service_always_has_a_predictable_cluster_dns_name()
    {
        var service = new Service { Name = "api", Namespace = "prod" };

        Assert.Equal("api.prod.svc.cluster.local", service.ClusterDnsName);
    }

    [Fact]
    public void Pod_with_hostname_and_subdomain_resolves_the_statefulset_pattern()
    {
        var pod = new Pod
        {
            Name = "db-0",
            Namespace = "prod",
            Hostname = "db-0",
            Subdomain = "db-headless",
        };

        Assert.Equal("db-0.db-headless.prod.svc.cluster.local", pod.ClusterDnsName);
    }

    [Fact]
    public void Pod_without_hostname_or_subdomain_has_no_cluster_dns_name()
    {
        var pod = new Pod { Name = "worker-abc12", Namespace = "prod" };

        Assert.Equal(string.Empty, pod.ClusterDnsName);
    }
}
