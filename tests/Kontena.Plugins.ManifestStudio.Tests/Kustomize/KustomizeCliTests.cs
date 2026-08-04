using Kontena.Plugins.ManifestStudio.Kustomize;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.ManifestStudio.Tests.Kustomize;

public sealed class KustomizeCliTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-kustomize-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WithKustomization()
    {
        File.WriteAllText(Path.Combine(_root, "kustomization.yaml"), "resources:\n  - deployment.yaml\n");
        return _root;
    }

    [Fact]
    public async Task A_directory_without_a_kustomization_file_fails_before_running_anything()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kustomize);
        var cli = new KustomizeCli(runner);

        var result = await cli.BuildAsync(_root);

        Assert.False(result.Ok);
        Assert.Contains("kustomization.yaml", result.Error, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task A_missing_directory_fails_before_running_anything()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kustomize);
        var cli = new KustomizeCli(runner);

        var result = await cli.BuildAsync(Path.Combine(_root, "does-not-exist"));

        Assert.False(result.Ok);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task A_standalone_kustomize_is_preferred_and_run_with_build()
    {
        var path = WithKustomization();
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kustomize)
            .Install(KnownTools.Kubectl)
            .When(i => i.Tool.Executable == "kustomize", output: ["kind: Deployment"]);
        var cli = new KustomizeCli(runner);

        var result = await cli.BuildAsync(path);

        Assert.True(result.Ok);
        Assert.Equal("kind: Deployment", result.Yaml);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("kustomize", invocation.Tool.Executable);
        Assert.Equal(["build", path], invocation.Arguments);
    }

    [Fact]
    public async Task Falls_back_to_kubectl_kustomize_when_kustomize_is_not_installed()
    {
        var path = WithKustomization();
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kubectl)
            .When(i => i.Tool.Executable == "kubectl", output: ["kind: Deployment"]);
        var cli = new KustomizeCli(runner);

        var result = await cli.BuildAsync(path);

        Assert.True(result.Ok);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("kubectl", invocation.Tool.Executable);
        Assert.Equal(["kustomize", path], invocation.Arguments);
    }

    [Fact]
    public async Task Neither_tool_installed_fails_with_an_explanation()
    {
        var cli = new KustomizeCli(new FakeToolRunner());

        var result = await cli.BuildAsync(WithKustomization());

        Assert.False(result.Ok);
        Assert.Contains("kustomize", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kubectl", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plugin_refusal_gets_kontenas_explanation_appended()
    {
        var path = WithKustomization();
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kustomize)
            .When(i => true, errorOutput: ["Error: unknown flag --enable-helm"], exitCode: 1);
        var cli = new KustomizeCli(runner);

        var result = await cli.BuildAsync(path);

        Assert.False(result.Ok);
        Assert.Contains("--enable-helm", result.Error, StringComparison.Ordinal);
        Assert.Contains("Helm inflator", result.Error, StringComparison.Ordinal);
    }
}
