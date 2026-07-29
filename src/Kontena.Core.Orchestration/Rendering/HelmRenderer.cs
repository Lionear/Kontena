using Kontena.Core.Tooling;

using System.Text.Json;

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>A chart to render, with the values that shape it.</summary>
public sealed record HelmRequest : RenderRequest
{
    /// <summary>A chart directory, a packaged <c>.tgz</c>, <c>repo/chart</c>, or an <c>oci://</c> reference.</summary>
    public required string Chart { get; init; }

    /// <summary>The release name templates see as <c>.Release.Name</c>.</summary>
    public required string ReleaseName { get; init; }

    /// <summary>Chart version; empty takes the newest the repository offers.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Values files, in precedence order — later files win, as with repeated <c>-f</c>.</summary>
    public IReadOnlyList<string> ValuesFiles { get; init; } = [];

    /// <summary>Individual overrides as <c>key=value</c>, applied after the files.</summary>
    public IReadOnlyList<string> Sets { get; init; } = [];

    /// <summary>Include the chart's CRDs in the output; they are part of what an install would create.</summary>
    public bool IncludeCrds { get; init; } = true;

    /// <summary>Run <c>helm lint</c> alongside the render (local charts only).</summary>
    public bool Lint { get; init; } = true;
}

/// <summary>
/// Renders a Helm chart to flat manifests (KON-89), so a chart plus its values can be reviewed
/// through the same dry-run and diff as any other bundle — the pre-flight side of Helm, without
/// installing or upgrading anything.
/// <para>
/// This is <c>helm template</c>: a purely local render. It does not reach the cluster, so
/// <c>.Capabilities</c> and <c>lookup</c> see nothing — a chart that branches on either renders
/// its offline shape. Cluster truth arrives one step later, when the rendered bundle goes through
/// the server-side dry-run (KON-86), which is where admission and validation have their say.
/// </para>
/// </summary>
public sealed class HelmRenderer : IManifestRenderer<HelmRequest>
{
    public string Name => "Helm";

    public string? Locate() => Cli.Locate("helm");

    public async ValueTask<RenderResult> RenderAsync(HelmRequest request, CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe is null)
            return RenderResult.Failed("helm template", NotInstalled);

        var chart = request.Chart?.Trim() ?? string.Empty;
        var release = request.ReleaseName?.Trim() ?? string.Empty;

        if (chart.Length == 0)
            return RenderResult.Failed("helm template", "Choose a chart: a directory, a packaged chart, or repo/name.");

        if (release.Length == 0)
            return RenderResult.Failed("helm template", "A release name is required — templates render it into resource names.");

        if (HelmArguments.RenderProblem(chart, release, request.Version, request.Sets) is { } unsafeValue)
            return RenderResult.Failed("helm template", unsafeValue);

        var missing = request.ValuesFiles.Where(f => f.Length > 0 && !File.Exists(f)).ToList();
        if (missing.Count > 0)
            return RenderResult.Failed("helm template", $"Values file not found: {string.Join(", ", missing)}");

        var args = BuildArgs(request, chart, release);
        var command = Cli.Describe(exe, args);

        CliResult result;
        try
        {
            result = await Cli.RunAsync(exe, args, ct: ct);
        }
        catch (ToolNotFoundException)
        {
            return RenderResult.Failed(command, NotInstalled);
        }

        if (!result.Ok)
            return RenderResult.Failed(command, result.Complaint);

        var docs = ManifestScan.Split(result.StdOut);
        var diagnostics = new List<RenderDiagnostic>();

        // helm's own notes (deprecated APIs, skipped hooks) arrive on stderr with a zero exit.
        diagnostics.AddRange(result.StdErr
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => new RenderDiagnostic(RenderSeverity.Warning, l, "helm")));

        if (request.Lint && Directory.Exists(chart))
            diagnostics.AddRange(await LintAsync(exe, request, chart, ct));

        diagnostics.AddRange(ManifestScan.Check(docs));

        return new RenderResult
        {
            Yaml = result.StdOut.Trim('\n'),
            Command = command,
            DocumentCount = docs.Count,
            Diagnostics = [.. diagnostics.OrderByDescending(d => d.Severity)],
        };
    }

    private const string NotInstalled =
        "'helm' was not found on PATH. Install Helm to render charts.";

    private static List<string> BuildArgs(HelmRequest request, string chart, string release)
    {
        var args = new List<string> { "template", release, chart };

        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            args.Add("--namespace");
            args.Add(request.Namespace.Trim());
        }

        if (request.Version.Length > 0)
        {
            args.Add("--version");
            args.Add(request.Version);
        }

        // Order matters: helm applies -f files left to right, then --set on top.
        foreach (var file in request.ValuesFiles.Where(f => f.Length > 0))
        {
            args.Add("--values");
            args.Add(file);
        }

        foreach (var set in request.Sets.Where(s => s.Length > 0))
        {
            args.Add("--set");
            args.Add(set);
        }

        if (request.IncludeCrds)
            args.Add("--include-crds");

        return args;
    }

    /// <summary>
    /// <c>helm lint</c> catches what a render cannot: a chart that templates cleanly but has a
    /// broken Chart.yaml, an unused values schema, a missing icon. Only meaningful for a chart on
    /// disk — a repository chart has already been packaged.
    /// </summary>
    private static async Task<IReadOnlyList<RenderDiagnostic>> LintAsync(
        string exe, HelmRequest request, string chart, CancellationToken ct)
    {
        var args = new List<string> { "lint", chart };
        foreach (var file in request.ValuesFiles.Where(f => f.Length > 0))
        {
            args.Add("--values");
            args.Add(file);
        }

        CliResult result;
        try
        {
            result = await Cli.RunAsync(exe, args, ct: ct);
        }
        catch (ToolNotFoundException)
        {
            return [];
        }

        // A lint failure is not a render failure: the manifests are there either way, and the
        // dry-run still gets to run. Report what lint said and let the user judge.
        return [.. result.StdOut
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('['))
            .Select(Parse)];

        static RenderDiagnostic Parse(string line)
        {
            // Lint's own [ERROR] lands as a warning here: the manifests exist either way, and
            // blocking the dry-run over a chart-metadata complaint would help nobody.
            var severity = line.StartsWith("[INFO]", StringComparison.Ordinal)
                ? RenderSeverity.Info
                : RenderSeverity.Warning;

            var text = line.IndexOf(']', StringComparison.Ordinal) is var end && end >= 0
                ? line[(end + 1)..].Trim()
                : line;

            return new RenderDiagnostic(severity, text, "helm lint");
        }
    }
}

/// <summary>A chart repository helm knows about.</summary>
public sealed record HelmRepo(string Name, string Url);

/// <summary>A chart offered by a repository.</summary>
/// <param name="Name">Qualified as <c>repo/chart</c> — exactly what a render takes as its chart.</param>
/// <param name="Version">Chart version.</param>
/// <param name="AppVersion">Version of the application the chart deploys.</param>
/// <param name="Description">One-line description from the chart metadata.</param>
public sealed record HelmChart(string Name, string Version, string AppVersion, string Description)
{
    /// <summary>The repository half of the name.</summary>
    public string Repo => Name.Split('/') is [var repo, _] ? repo : string.Empty;

    /// <summary>The chart half of the name.</summary>
    public string ShortName => Name.Split('/') is [_, var chart] ? chart : Name;
}

/// <summary>
/// Read access to the local Helm repository configuration, so the chart field can be a picker
/// rather than something to type from memory. This drives helm's own config — the same repos the
/// user's terminal sees — rather than keeping a list of its own.
/// <para>
/// Private repositories and OCI registries need credentials, which belong in the keychain
/// (KON-52); until then this covers repositories that need no authentication.
/// </para>
/// </summary>
public static class HelmRepos
{
    public static bool IsAvailable => Cli.Locate("helm") is not null;

    /// <summary>The configured repositories, newest config wins. Empty when helm has none.</summary>
    public static async ValueTask<IReadOnlyList<HelmRepo>> ListAsync(CancellationToken ct = default)
    {
        // `helm repo list` exits non-zero when there are no repositories at all — an empty list,
        // not an error.
        var result = await TryRunAsync(["repo", "list", "-o", "json"], ct);
        if (result is not { Ok: true })
            return [];

        return [.. HelmJson.Read(result.Value.StdOut, e => new HelmRepo(
            HelmJson.String(e, "name"),
            HelmJson.String(e, "url")))];
    }

    /// <summary>
    /// Charts matching <paramref name="term"/> across the configured repositories; an empty term
    /// lists everything they offer.
    /// </summary>
    public static async ValueTask<IReadOnlyList<HelmChart>> SearchAsync(string term = "", CancellationToken ct = default)
    {
        // The caller shows the reason; here the term is simply not passed on to helm.
        if (HelmArguments.SearchProblem(term) is not null)
            return [];

        var args = new List<string> { "search", "repo" };
        if (!string.IsNullOrWhiteSpace(term))
            args.Add(term.Trim());
        args.AddRange(["-o", "json"]);

        var result = await TryRunAsync(args, ct);
        if (result is not { Ok: true })
            return [];

        return [.. HelmJson.Read(result.Value.StdOut, e => new HelmChart(
            HelmJson.String(e, "name"),
            HelmJson.String(e, "version"),
            HelmJson.String(e, "app_version"),
            HelmJson.String(e, "description")))];
    }

    /// <summary>Refresh the local index of every repository. Returns what helm reported.</summary>
    public static async ValueTask<string> UpdateAsync(CancellationToken ct = default)
    {
        var result = await TryRunAsync(["repo", "update"], ct);
        return result is null
            ? "'helm' was not found on PATH."
            : result.Value.Ok ? "Repositories updated." : result.Value.Complaint;
    }

    /// <summary>
    /// Add a repository under <paramref name="name"/>. Credentials are out of scope (KON-52), so
    /// this only works for repositories that serve their index anonymously.
    /// </summary>
    public static async ValueTask<string?> AddAsync(string name, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            return "A repository needs both a name and a URL.";

        if (HelmArguments.RepositoryProblem(name, url) is { } problem)
            return problem;

        var result = await TryRunAsync(["repo", "add", name.Trim(), url.Trim(), "--force-update"], ct);
        return result is null ? "'helm' was not found on PATH."
            : result.Value.Ok ? null : result.Value.Complaint;
    }

    /// <summary>Remove a repository. Returns null on success, or what helm complained about.</summary>
    public static async ValueTask<string?> RemoveAsync(string name, CancellationToken ct = default)
    {
        if (HelmArguments.OptionLike("repository name", name) is { } problem)
            return problem;

        var result = await TryRunAsync(["repo", "remove", name.Trim()], ct);
        return result is null ? "'helm' was not found on PATH."
            : result.Value.Ok ? null : result.Value.Complaint;
    }

    private static async ValueTask<CliResult?> TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = Cli.Locate("helm");
        if (exe is null)
            return null;

        try
        {
            return await Cli.RunAsync(exe, args, ct: ct);
        }
        catch (ToolNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>Just enough JSON reading for helm's <c>-o json</c> output.</summary>
internal static class HelmJson
{
    public static IReadOnlyList<T> Read<T>(string json, Func<JsonElement, T> map)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Select(map)]
                : [];
        }
    }

    public static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
