using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration.Rendering;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The handover the render sources exist for (KON-88, KON-89 → KON-86): a kustomization or a chart
/// becomes flat YAML, and from there it is an ordinary bundle that the real apiserver validates.
/// Renders the repository's own samples, so a sample that stops building fails the build too.
/// <para>
/// Dry-run only. These tests must never leave anything behind on the cluster they find.
/// </para>
/// </summary>
public class RenderedBundleTests
{
    [SkippableFact]
    public async Task A_kustomize_overlay_survives_the_round_trip_to_the_apiserver()
    {
        var renderer = new KustomizeRenderer();
        Skip.If(renderer.Locate() is null, "Neither kustomize nor kubectl is installed.");

        var result = await renderer.RenderAsync(new KustomizeRequest
        {
            Path = RepoPath("samples/kustomize/overlays/prod"),
        });

        Assert.True(result.Ok, Explain(result));

        var plan = await DryRunAsync(result.Yaml);

        // The overlay's prefix is what tells the rendered objects apart from the base's.
        Assert.Contains(plan, p => p.Resource.Kind.Kind == "Deployment" && p.Resource.Name == "prod-guestbook");
        Assert.Contains(plan, p => p.Resource.Kind.Kind == "Service" && p.Resource.Name == "prod-guestbook");
        Assert.DoesNotContain(plan, p => p.Action == ApplyAction.Failed);
    }

    [SkippableFact]
    public async Task A_chart_survives_the_round_trip_to_the_apiserver()
    {
        var renderer = new HelmRenderer();
        Skip.If(renderer.Locate() is null, "helm is not installed.");

        var chart = RepoPath("samples/helm/guestbook");
        var result = await renderer.RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "kontena-test",
            Namespace = "default",
            ValuesFiles = [Path.Combine(chart, "values-prod.yaml")],
            Sets = ["replicaCount=2"],
        });

        Assert.True(result.Ok, Explain(result));
        Assert.Contains("replicas: 2", result.Yaml, StringComparison.Ordinal);

        var plan = await DryRunAsync(result.Yaml);

        Assert.Contains(plan, p => p.Resource.Kind.Kind == "Deployment" && p.Resource.Name == "kontena-test-guestbook");
        Assert.DoesNotContain(plan, p => p.Action == ApplyAction.Failed);
    }

    [SkippableFact]
    public async Task A_render_the_apiserver_rejects_is_reported_per_resource()
    {
        var renderer = new HelmRenderer();
        Skip.If(renderer.Locate() is null, "helm is not installed.");

        var chart = RepoPath("samples/helm/guestbook");
        var result = await renderer.RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "kontena-test",
            Namespace = "default",

            // A render is happy with this; only the apiserver knows replicas cannot be negative.
            Sets = ["replicaCount=-1"],
            Lint = false,
        });

        Assert.True(result.Ok, Explain(result));

        var deployment = Assert.Single(await DryRunAsync(result.Yaml), p => p.Resource.Kind.Kind == "Deployment");

        Assert.Equal(ApplyAction.Failed, deployment.Action);
        Assert.Contains("replicas", deployment.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<ApplyProgress>> DryRunAsync(string yaml)
    {
        var provider = KubernetesClusterProvider.DiscoverAll().FirstOrDefault();
        Skip.If(provider is null, "No Kubernetes context in the kubeconfig.");

        using var engine = (KubernetesClusterEngine)provider!.CreateBackend();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await engine.PingAsync(cts.Token);
        }
        catch (Exception)
        {
            Skip.If(true, "No reachable Kubernetes cluster in the kubeconfig.");
        }

        var plan = new List<ApplyProgress>();
        await foreach (var progress in engine.ApplyAsync(new ManifestBundle { Yaml = yaml, DryRun = true }))
            plan.Add(progress);

        return plan;
    }

    /// <summary>The repository root is wherever <c>samples</c> lives above the test binary.</summary>
    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;

        Skip.If(dir is null, "Could not locate the repository's samples directory.");
        return Path.Combine(dir!.FullName, relative);
    }

    private static string Explain(RenderResult result) =>
        result.Command + "\n" + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}"));
}
