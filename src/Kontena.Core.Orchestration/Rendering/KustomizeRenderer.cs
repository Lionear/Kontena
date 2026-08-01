using Kontena.Sdk.Tooling;
using Kontena.Core.Orchestration;

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>
/// A kustomization to build: a directory holding a <c>kustomization.yaml</c>.
/// <see cref="RenderRequest.Namespace"/> is not used — a kustomization declares its own namespace,
/// and overriding it would mean editing the user's files.
/// </summary>
public sealed record KustomizeRequest : RenderRequest
{
    /// <summary>The overlay or base directory — <c>overlays/prod</c>, not the repository root.</summary>
    public required string Path { get; init; }
}

/// <summary>
/// Builds a kustomization into flat manifests (KON-88), so overlays can be reviewed through the
/// same dry-run and diff as any other bundle. Overlay mistakes — a patch that matches nothing, a
/// missing base — surface here, before the cluster is ever asked.
/// <para>
/// Prefers a standalone <c>kustomize</c> and falls back to <c>kubectl kustomize</c>, which every
/// machine that talks to a cluster already has. Plugins (exec, KRM, the Helm inflator) stay off:
/// enabling them lets a kustomization run arbitrary code during what the user asked for as a
/// preview, and a preview must not be able to do that. A kustomization that needs them fails with
/// kustomize's own message, which is passed straight through.
/// </para>
/// </summary>
public sealed class KustomizeRenderer : IManifestRenderer<KustomizeRequest>
{
    public string Name => "Kustomize";

    /// <summary>File names kustomize accepts as the root of a kustomization.</summary>
    private static readonly string[] RootFiles = ["kustomization.yaml", "kustomization.yml", "Kustomization"];

    public string? Locate() => Cli.Locate("kustomize") ?? Cli.Locate("kubectl");

    public async ValueTask<RenderResult> RenderAsync(KustomizeRequest request, CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe is null)
        {
            return RenderResult.Failed(
                "kustomize build",
                "Neither 'kustomize' nor 'kubectl' was found on PATH. Install either one to build kustomizations.");
        }

        var path = request.Path?.Trim() ?? string.Empty;
        if (path.Length == 0)
            return RenderResult.Failed("kustomize build", "Choose the directory that holds the kustomization.");

        if (!Directory.Exists(path))
            return RenderResult.Failed("kustomize build", $"'{path}' is not a directory.");

        if (!RootFiles.Any(f => File.Exists(System.IO.Path.Combine(path, f))))
        {
            return RenderResult.Failed(
                "kustomize build",
                $"'{path}' has no kustomization.yaml. Point at the overlay directory, not the repository root.");
        }

        // `kustomize build <dir>` and `kubectl kustomize <dir>` are the same renderer, reached by
        // different verbs.
        var viaKubectl = System.IO.Path.GetFileNameWithoutExtension(exe)
            .Equals("kubectl", StringComparison.OrdinalIgnoreCase);
        var args = viaKubectl ? new[] { "kustomize", path } : ["build", path];
        var command = Cli.Describe(exe, args);

        CliResult result;
        try
        {
            result = await Cli.RunAsync(exe, args, ct: ct);
        }
        catch (ToolNotFoundException ex)
        {
            return RenderResult.Failed(command, ex.Message);
        }

        if (!result.Ok)
            return RenderResult.Failed(command, Explain(result.Complaint));

        var docs = ManifestScan.Split(result.StdOut);
        return new RenderResult
        {
            Yaml = result.StdOut.Trim('\n'),
            Command = command,
            DocumentCount = docs.Count,
            Diagnostics = [.. Warnings(result.StdErr, "kustomize"), .. ManifestScan.Check(docs)],
        };
    }

    /// <summary>
    /// Kustomize's plugin refusals name a flag we deliberately do not pass; say so, rather than
    /// leaving the user to wonder why the same build works in their terminal.
    /// </summary>
    private static string Explain(string complaint)
    {
        if (complaint.Contains("--enable-helm", StringComparison.Ordinal))
        {
            return complaint
                + "\n\nKontena builds kustomizations without the Helm inflator: it would run a chart's "
                + "templates during a preview. Render the chart through the Helm source instead.";
        }

        if (complaint.Contains("--enable-alpha-plugins", StringComparison.Ordinal)
            || complaint.Contains("--enable-exec", StringComparison.Ordinal))
        {
            return complaint
                + "\n\nKontena builds kustomizations without plugins, which can execute arbitrary code. "
                + "Build this one with the kustomize CLI if you trust it, and paste the output here.";
        }

        return complaint;
    }

    /// <summary>Kustomize deprecation notices land on stderr while the build still succeeds.</summary>
    private static IEnumerable<RenderDiagnostic> Warnings(string stderr, string source) => stderr
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .Select(l => new RenderDiagnostic(RenderSeverity.Warning, l, source));
}
