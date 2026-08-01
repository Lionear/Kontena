using Kontena.Core.Orchestration.Rendering;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// The renderers drive real CLIs, so these drive them too — a mocked <c>helm template</c> would
/// only prove that the mock agrees with itself. Where the tool is missing the test skips, the way
/// the cluster tests do.
/// <para>
/// That includes the tests asserting a request is rejected <em>before</em> the tool runs: both
/// renderers look for their executable first, so a machine without one answers "install kustomize"
/// rather than "this directory has no kustomization.yaml". The order is deliberate — a missing tool
/// is the blocker the user has to clear either way — so these skip too rather than assert a message
/// they cannot reach.
/// </para>
/// </summary>
public class RendererTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kontena-render-").FullName;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is not worth failing over.
        }
    }

    // ── Kustomize (KON-88) ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_directory_without_a_kustomization_fails_before_the_tool_runs()
    {
        SkipIfMissing(new KustomizeRenderer());

        var result = await new KustomizeRenderer().RenderAsync(new KustomizeRequest { Path = _root });

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("no kustomization.yaml", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_path_that_is_not_there_fails_with_the_path_in_the_message()
    {
        SkipIfMissing(new KustomizeRenderer());

        var missing = Path.Combine(_root, "nope");

        var result = await new KustomizeRenderer().RenderAsync(new KustomizeRequest { Path = missing });

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains(missing, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_overlay_renders_with_its_patches_applied()
    {
        SkipIfMissing(new KustomizeRenderer());

        WriteBase();
        var overlay = Path.Combine(_root, "overlays", "prod");
        Directory.CreateDirectory(overlay);
        Write(Path.Combine(overlay, "kustomization.yaml"), """
            resources:
              - ../../base
            namePrefix: prod-
            replicas:
              - name: web
                count: 4
            """);

        var result = await new KustomizeRenderer().RenderAsync(new KustomizeRequest { Path = overlay });

        Assert.True(result.Ok, Explain(result));
        Assert.Equal(1, result.DocumentCount);

        var doc = Assert.Single(ManifestScan.Split(result.Yaml));
        Assert.Equal("prod-web", doc.Name);
        Assert.Contains("replicas: 4", result.Yaml, StringComparison.Ordinal);

        // Whichever tool was found, the command is the one a user could rerun in a terminal.
        Assert.Contains("kustomize", result.Command, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_overlay_pointing_at_a_base_that_is_not_there_fails_in_kustomizes_own_words()
    {
        SkipIfMissing(new KustomizeRenderer());

        var overlay = Path.Combine(_root, "overlays", "broken");
        Directory.CreateDirectory(overlay);
        Write(Path.Combine(overlay, "kustomization.yaml"), """
            resources:
              - ../../base
            namePrefix: broken-
            """);

        var result = await new KustomizeRenderer().RenderAsync(new KustomizeRequest { Path = overlay });

        Assert.False(result.Ok, "an overlay whose base is missing should not render clean");

        var error = Assert.Single(result.Diagnostics, d => d.Severity == RenderSeverity.Error);
        Assert.Contains("base", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helm (KON-89) ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_render_without_a_release_name_says_why_it_needs_one()
    {
        SkipIfMissing(new HelmRenderer());

        var result = await new HelmRenderer().RenderAsync(new HelmRequest { Chart = _root, ReleaseName = "  " });

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("release name", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_values_file_that_is_not_there_is_caught_before_helm_runs()
    {
        SkipIfMissing(new HelmRenderer());

        var result = await new HelmRenderer().RenderAsync(new HelmRequest
        {
            Chart = _root,
            ReleaseName = "checkout",
            ValuesFiles = [Path.Combine(_root, "values-prod.yaml")],
        });

        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Values file not found", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_chart_renders_with_its_values_and_the_release_name()
    {
        var helm = SkipIfMissing(new HelmRenderer());
        var chart = await CreateChartAsync(helm);

        var result = await new HelmRenderer().RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "checkout",
            Namespace = "retail",
            Sets = ["replicaCount=3"],
        });

        Assert.True(result.Ok, Explain(result));

        // The chart is called "shop"; the release name is what tells two installs of it apart.
        var docs = ManifestScan.Split(result.Yaml);
        Assert.Contains(docs, d => d.Kind == "Deployment" && d.Name == "checkout-shop");
        Assert.Contains(docs, d => d.Kind == "Service" && d.Name == "checkout-shop");
        Assert.Contains("replicas: 3", result.Yaml, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Values_files_lose_to_a_later_set()
    {
        var helm = SkipIfMissing(new HelmRenderer());
        var chart = await CreateChartAsync(helm);

        var values = Path.Combine(_root, "values-prod.yaml");
        Write(values, "replicaCount: 7\n");

        var result = await new HelmRenderer().RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "checkout",
            ValuesFiles = [values],
            Sets = ["replicaCount=9"],
        });

        Assert.True(result.Ok, Explain(result));
        Assert.Contains("replicas: 9", result.Yaml, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_template_that_cannot_render_fails_with_helms_message()
    {
        var helm = SkipIfMissing(new HelmRenderer());
        var chart = await CreateChartAsync(helm);

        // A value the chart never defines — the kind of mistake a values file makes.
        Write(Path.Combine(chart, "templates", "extra.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{ .Values.database.host }}
            """);

        var result = await new HelmRenderer().RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "checkout",
            Lint = false,
        });

        Assert.False(result.Ok);

        // Helm's own message, not a message of ours: it names the template and the line.
        var error = Assert.Single(result.Diagnostics, d => d.Severity == RenderSeverity.Error);
        Assert.Contains("extra.yaml", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Lint_findings_ride_along_without_blocking_the_render()
    {
        var helm = SkipIfMissing(new HelmRenderer());
        var chart = await CreateChartAsync(helm);

        var result = await new HelmRenderer().RenderAsync(new HelmRequest
        {
            Chart = chart,
            ReleaseName = "checkout",
            Lint = true,
        });

        Assert.True(result.Ok, Explain(result));
        Assert.Contains(result.Diagnostics, d => d.Source == "helm lint");
    }

    [SkippableFact]
    public async Task Repositories_come_from_helms_own_configuration()
    {
        Skip.IfNot(HelmRepos.IsAvailable, "helm is not installed");

        // Whatever this machine has configured — the point is that the shape parses.
        foreach (var repo in await HelmRepos.ListAsync())
        {
            Assert.NotEmpty(repo.Name);
            Assert.NotEmpty(repo.Url);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string SkipIfMissing(IManifestRenderer renderer)
    {
        var exe = renderer.Locate();
        Skip.If(exe is null, $"{renderer.Name} is not installed");
        return exe!;
    }

    /// <summary>A scaffolded chart — helm's own starter, so the fixture is never out of date.</summary>
    private async Task<string> CreateChartAsync(string helm)
    {
        var charts = Path.Combine(_root, "charts");
        Directory.CreateDirectory(charts);

        var create = await Cli.RunAsync(helm, ["create", "shop"], charts);
        Skip.IfNot(create.Ok, $"helm create failed: {create.Complaint}");

        return Path.Combine(charts, "shop");
    }

    private void WriteBase()
    {
        var @base = Path.Combine(_root, "base");
        Directory.CreateDirectory(@base);

        Write(Path.Combine(@base, "kustomization.yaml"), """
            resources:
              - deployment.yaml
            """);

        Write(Path.Combine(@base, "deployment.yaml"), """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: web
            spec:
              replicas: 1
              selector:
                matchLabels:
                  app: web
              template:
                metadata:
                  labels:
                    app: web
                spec:
                  containers:
                    - name: web
                      image: nginx:1.27-alpine
            """);
    }

    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static string Explain(RenderResult result) =>
        result.Command + "\n" + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}"));
}
