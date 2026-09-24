using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The helm command lines behind the Releases page (KON-473), and the two things helm writes in its
/// own shape: chart-with-version in one string, and Go timestamps.
/// </summary>
public class HelmCliTests
{
    private readonly FakeToolRunner _runner = new FakeToolRunner().Install(KnownTools.Helm);

    private HelmCli Helm(string? kubeconfig = "/home/me/.kube/work") => new(_runner, () => "prod", kubeconfig);

    private IReadOnlyList<string> Only() => Assert.Single(_runner.Invocations).Arguments;

    [Fact]
    public async Task Every_call_targets_the_engines_context_and_kubeconfig()
    {
        _runner.When(_ => true, ["[]"]);

        await Helm().ListAsync();

        var args = Only();
        Assert.Equal("--kube-context=prod", args[0]);
        Assert.Equal("--kubeconfig=/home/me/.kube/work", args[1]);
    }

    [Fact]
    public async Task The_default_kubeconfig_is_left_to_helm()
    {
        _runner.When(_ => true, ["[]"]);

        await Helm(kubeconfig: null).ListAsync();

        Assert.DoesNotContain(Only(), a => a.StartsWith("--kubeconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_reads_every_namespace_and_every_status()
    {
        _runner.When(_ => true,
        [
            """[{"name":"web","namespace":"apps","revision":"3","updated":"2024-05-01 10:11:12.123456789 +0200 CEST","status":"failed","chart":"ingress-nginx-4.10.1","app_version":"1.10.1"}]""",
        ]);

        var release = Assert.Single(await Helm().ListAsync());

        Assert.Contains("--all-namespaces", Only());
        Assert.Contains("--all", Only()); // failed and pending releases are the ones worth seeing
        Assert.Equal("web", release.Name);
        Assert.Equal("apps", release.Namespace);
        Assert.Equal("ingress-nginx", release.Chart);
        Assert.Equal("4.10.1", release.ChartVersion);
        Assert.Equal(3, release.Revision);
        Assert.Equal("failed", release.Status);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 8, 11, 12, TimeSpan.Zero), release.Updated!.Value.ToUniversalTime().AddTicks(-1234567));
    }

    [Fact]
    public async Task A_failing_read_surfaces_helms_complaint()
    {
        _runner.When(_ => true, exitCode: 1, errorOutput: ["Error: Kubernetes cluster unreachable"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Helm().ListAsync().AsTask());

        Assert.Equal("Error: Kubernetes cluster unreachable", ex.Message);
    }

    [Fact]
    public async Task Values_without_any_read_back_as_empty_not_as_null()
    {
        _runner.When(_ => true, ["null"]);

        Assert.Equal(string.Empty, await Helm().GetValuesAsync("web", "apps"));
    }

    [Fact]
    public async Task Positionals_sit_behind_a_double_dash_so_they_are_never_read_as_options()
    {
        _runner.When(_ => true);

        await Helm().UninstallAsync("--kubeconfig=/tmp/theirs", "apps");

        var args = Only();
        Assert.Equal(["uninstall", "--namespace=apps", "--", "--kubeconfig=/tmp/theirs"], args.Skip(2));
    }

    [Fact]
    public async Task Rollback_names_the_revision()
    {
        _runner.When(_ => true);

        Assert.Null(await Helm().RollbackAsync("web", "apps", 2));
        Assert.Equal(["rollback", "--namespace=apps", "--", "web", "2"], Only().Skip(2));
    }

    [Fact]
    public async Task Upgrade_hands_the_values_over_in_a_file_that_is_gone_afterwards()
    {
        string? written = null;
        _runner.When(i =>
        {
            var file = i.Arguments.Single(a => a.StartsWith("--values=", StringComparison.Ordinal))["--values=".Length..];
            written = File.ReadAllText(file);
            return true;
        });

        var error = await Helm().UpgradeAsync(new HelmUpgrade
        {
            Release = "web", Namespace = "apps", Chart = "ingress-nginx/ingress-nginx", Version = "4.11.0",
            ValuesYaml = "replicaCount: 2\n",
        });

        Assert.Null(error);
        Assert.Equal("replicaCount: 2\n", written);
        var args = Only();
        Assert.Contains("--version=4.11.0", args);
        Assert.Equal(["--", "web", "ingress-nginx/ingress-nginx"], args.TakeLast(3));
        Assert.False(File.Exists(args.Single(a => a.StartsWith("--values=", StringComparison.Ordinal))["--values=".Length..]));
    }

    [Fact]
    public async Task A_failed_write_returns_helms_words()
    {
        _runner.When(_ => true, exitCode: 1, errorOutput: ["Error: release: not found"]);

        Assert.Equal("Error: release: not found", await Helm().UninstallAsync("web", "apps"));
    }

    [Theory]
    [InlineData("ingress-nginx-4.10.1", "ingress-nginx", "4.10.1")]
    [InlineData("k8s-dashboard-7.0.0-rc.1", "k8s-dashboard", "7.0.0-rc.1")]
    [InlineData("cert-manager-v1.14.4", "cert-manager", "v1.14.4")]
    [InlineData("nochart", "nochart", "")]
    public void Chart_and_version_come_apart(string chart, string name, string version)
    {
        Assert.Equal((name, version), HelmCli.SplitChart(chart));
    }

    [Theory]
    [InlineData("2024-05-01 10:11:12.123456789 +0200 CEST")]
    [InlineData("2024-05-01T10:11:12.123456789+02:00")]
    public void Both_of_helms_time_layouts_parse(string text)
    {
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 8, 11, 12, TimeSpan.Zero), HelmCli.ParseTime(text)!.Value.ToUniversalTime().AddTicks(-1234567));
    }

    [Fact]
    public void An_unreadable_time_is_no_time()
    {
        Assert.Null(HelmCli.ParseTime("yesterday"));
    }
}
