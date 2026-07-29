using Kontena.Core.Orchestration.Rendering;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// Values helm would read as its own options (KON-182). The rule lives next to the command lines it
/// protects, so these test it there: the render, the repository commands, and the chart search.
/// <para>
/// Every refusal has its counterproof here — <c>bitnami/nginx</c>, <c>my-release</c>, an
/// <c>oci://</c> registry and a plain search term must keep working. A rule that is too strict
/// breaks ordinary use more quietly than one that is too loose.
/// </para>
/// </summary>
public class HelmArgumentsTests
{
    [Theory]
    [InlineData("--kubeconfig=/tmp/theirs")]
    [InlineData("-f/tmp/values.yaml")]
    public void A_chart_that_is_really_a_helm_option_is_refused_and_says_so(string chart)
    {
        var problem = HelmArguments.RenderProblem(chart, "shop", string.Empty, []);

        Assert.NotNull(problem);
        Assert.Contains("chart", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_name_that_is_really_a_helm_option_is_refused()
    {
        Assert.NotNull(HelmArguments.RenderProblem("./shop", "--namespace=kube-system", string.Empty, []));
    }

    [Fact]
    public void A_version_and_a_set_are_held_to_the_same_rule()
    {
        Assert.NotNull(HelmArguments.RenderProblem("bitnami/nginx", "web", "--ca-file=/tmp/ca.pem", []));
        Assert.NotNull(HelmArguments.RenderProblem("bitnami/nginx", "web", "1.2.3", ["--set-file=x"]));
    }

    [Fact]
    public void An_ordinary_render_is_accepted()
    {
        Assert.Null(HelmArguments.RenderProblem(
            "bitnami/nginx", "my-release", "1.2.3", ["image.tag=1.25", "replicaCount=2"]));

        Assert.Null(HelmArguments.RenderProblem("./charts/shop", "shop", string.Empty, []));
    }

    [Fact]
    public void A_repository_name_or_url_that_is_really_a_helm_option_is_refused()
    {
        Assert.NotNull(HelmArguments.RepositoryProblem("-oops", "https://charts.bitnami.com/bitnami"));
        Assert.NotNull(HelmArguments.RepositoryProblem("bitnami", "--ca-file=/tmp/ca.pem"));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("charts.bitnami.com/bitnami")]
    public void A_repository_url_on_another_scheme_is_refused(string url)
    {
        var problem = HelmArguments.RepositoryProblem("bitnami", url);

        Assert.NotNull(problem);
        Assert.Contains("http://", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://charts.bitnami.com/bitnami")]
    [InlineData("http://charts.internal/stable")]
    [InlineData("oci://registry-1.docker.io/bitnamicharts")]
    public void An_ordinary_repository_is_accepted(string url)
    {
        Assert.Null(HelmArguments.RepositoryProblem("bitnami", url));
    }

    [Fact]
    public void A_search_term_that_is_really_a_helm_option_is_refused_and_plain_terms_are_not()
    {
        Assert.NotNull(HelmArguments.SearchProblem("--kubeconfig=/tmp/theirs"));

        Assert.Null(HelmArguments.SearchProblem("ingress"));
        Assert.Null(HelmArguments.SearchProblem(string.Empty));
    }

    [Fact]
    public async Task Adding_a_repository_is_refused_before_helm_is_reached()
    {
        // No helm needed: the check runs before the process would start, so this holds on a machine
        // that has none.
        var problem = await HelmRepos.AddAsync("bitnami", "file:///etc/passwd");

        Assert.NotNull(problem);
        Assert.Contains("http://", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_repository_whose_name_is_an_option_is_refused_before_helm_is_reached()
    {
        Assert.NotNull(await HelmRepos.RemoveAsync("--kubeconfig=/tmp/theirs"));
    }

    [Fact]
    public async Task A_search_term_that_is_an_option_returns_nothing_rather_than_running()
    {
        Assert.Empty(await HelmRepos.SearchAsync("-oops"));
    }

    [SkippableFact]
    public async Task A_chart_that_is_an_option_fails_the_render_rather_than_reaching_helm()
    {
        var renderer = new HelmRenderer();
        Skip.If(renderer.Locate() is null, "helm is not installed");

        var result = await renderer.RenderAsync(new HelmRequest
        {
            Chart = "--kubeconfig=/tmp/theirs",
            ReleaseName = "shop",
        });

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("its own options", StringComparison.Ordinal));
    }
}
