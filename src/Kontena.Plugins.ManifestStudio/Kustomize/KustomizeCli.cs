using Kontena.Sdk.Tooling;

namespace Kontena.Plugins.ManifestStudio.Kustomize;

public sealed record KustomizeBuildResult
{
    public string? Yaml { get; private init; }
    public string? Error { get; private init; }
    public bool Ok => Error is null;

    public static KustomizeBuildResult Succeeded(string yaml) => new() { Yaml = yaml };
    public static KustomizeBuildResult Failed(string error) => new() { Error = error };
}

/// <summary>
/// Builds a kustomization into flat YAML by shelling out — the same tool preference (standalone
/// <c>kustomize</c>, falling back to <c>kubectl kustomize</c>) and the same "no plugins" security
/// posture as <c>Kontena.Core.Orchestration.Rendering.KustomizeRenderer</c> (KON-88), which this
/// plugin cannot reference: that project sits outside <c>Kontena.Sdk</c>, and a plugin may only
/// reference the Sdk (KON-190's rule, extended to plugins). What KON-88 actually does is drive
/// <c>Kontena.Sdk.Tooling</c> — already fully available here — so nothing about rendering itself is
/// reimplemented, only this thin call. Never passes <c>--enable-helm</c>/<c>--enable-exec</c>/
/// <c>--enable-alpha-plugins</c>, for the same reason KON-88 does not: a preview must not be able to
/// run arbitrary code.
/// </summary>
public sealed class KustomizeCli(IToolRunner? runner = null)
{
    private static readonly string[] RootFiles = ["kustomization.yaml", "kustomization.yml", "Kustomization"];

    private readonly IToolRunner _runner = runner ?? new ToolRunner();

    public async ValueTask<KustomizeBuildResult> BuildAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
            return KustomizeBuildResult.Failed($"'{path}' is not a directory.");

        if (!RootFiles.Any(f => File.Exists(Path.Combine(path, f))))
        {
            return KustomizeBuildResult.Failed(
                $"'{path}' has no kustomization.yaml. Point at the overlay directory, not the repository root.");
        }

        var kustomize = await _runner.FindAsync(KnownTools.Kustomize, ct).ConfigureAwait(false);
        if (kustomize.Found)
            return await RunAsync(KnownTools.Kustomize, ["build", path], ct).ConfigureAwait(false);

        var kubectl = await _runner.FindAsync(KnownTools.Kubectl, ct).ConfigureAwait(false);
        if (kubectl.Found)
            return await RunAsync(KnownTools.Kubectl, ["kustomize", path], ct).ConfigureAwait(false);

        return KustomizeBuildResult.Failed(
            "Neither 'kustomize' nor 'kubectl' was found on PATH. Install either one to build kustomizations.");
    }

    private async ValueTask<KustomizeBuildResult> RunAsync(
        ExternalTool tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(new ToolInvocation(tool, arguments), ct).ConfigureAwait(false);
            return result.Ok
                ? KustomizeBuildResult.Succeeded(result.StandardOutput.Trim('\n'))
                : KustomizeBuildResult.Failed(Explain(result.Complaint));
        }
        catch (ToolNotFoundException ex)
        {
            return KustomizeBuildResult.Failed(ex.Message);
        }
    }

    /// <summary>Kustomize's plugin refusals name a flag this CLI deliberately never passes; say so,
    /// same wording as KON-88's <c>KustomizeRenderer.Explain</c>.</summary>
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
}
