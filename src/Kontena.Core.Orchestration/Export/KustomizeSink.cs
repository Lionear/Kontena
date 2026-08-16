using Kontena.Core.Orchestration.Rendering;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Tooling;

namespace Kontena.Core.Orchestration.Export;

/// <summary>
/// Writes a bundle into a kustomization (KON-211): the file through <see cref="FileSink"/>, then
/// <c>kustomize edit add resource</c> so the kustomization lists it.
/// <para>
/// The <c>kustomization.yaml</c> is never parsed or edited here. kustomize owns that format, and a
/// hand-written YAML edit drops comments, reorders keys and eventually arrives as a bug report about
/// somebody's GitOps repository. The tool does it, or nobody does.
/// </para>
/// <para>
/// So when kustomize is missing, or refuses: the file stays written and the result carries the one
/// line to paste, with the path relative to the kustomization. The same choice the metrics-server
/// install makes when it meets an args block it does not recognise — half a job that says which half
/// beats a guess at somebody's repository.
/// </para>
/// </summary>
/// <param name="file">Where the manifest itself goes. Must be at or below
/// <paramref name="kustomizationDirectory"/>, since that is all a kustomization can list.</param>
/// <param name="kustomizationDirectory">The directory holding the <c>kustomization.yaml</c> — the
/// overlay, not the repository root.</param>
/// <param name="runner">The tool seam (KON-129); the default drives the real kustomize.</param>
public sealed class KustomizeSink(
    FileSink file, string kustomizationDirectory, IToolRunner? runner = null) : IManifestSink
{
    private readonly IToolRunner _runner = runner ?? new ToolRunner();

    public async ValueTask<SinkResult> WriteAsync(ManifestBundle bundle, CancellationToken ct = default)
    {
        string root, target;
        try
        {
            root = SinkPath.Real(kustomizationDirectory);
            target = SinkPath.Real(file.Directory);
        }
        catch (IOException ex)
        {
            return SinkResult.Refused($"Where these paths lead cannot be established: {ex.Message}");
        }

        if (!Directory.Exists(root))
            return SinkResult.Refused($"'{root}' is not a directory.");

        var kustomization = KustomizeRenderer.KustomizationFiles
            .Select(f => Path.Combine(root, f))
            .FirstOrDefault(File.Exists);

        if (kustomization is null)
        {
            return SinkResult.Refused(
                $"'{root}' has no kustomization.yaml. Point at the overlay directory, not the "
                + "repository root.");
        }

        // Checked before anything is written: a kustomization can only list resources at or below its
        // own directory, so writing the file first would leave one lying about that nothing will ever
        // reference. Real paths, so a directory that is a link out of the repository is caught here
        // rather than by kustomize's own security error.
        if (!SinkPath.Contains(root, target))
        {
            return SinkResult.Refused(
                $"'{target}' is outside '{root}'. A kustomization can only list files at or below its "
                + "own directory — write inside it, or point at a kustomization that covers this path.");
        }

        var written = await file.WriteAsync(bundle, ct);
        if (!written.Ok)
            return written;

        // Relative to the kustomization and with forward slashes: that is what goes in the file, on
        // every platform. An absolute path would work on this machine and nowhere else.
        var resource = Path.GetRelativePath(root, written.Path).Replace('\\', '/');
        var fallback = $"  - {resource}";
        var arguments = new[] { "edit", "add", "resource", resource };
        var command = $"{ToolCommand.Describe("cd", [root])} && "
            + ToolCommand.Describe(KnownTools.Kustomize.Executable, arguments);

        try
        {
            var result = await _runner.RunAsync(
                new ToolInvocation(KnownTools.Kustomize, arguments) { WorkingDirectory = root }, ct);

            if (!result.Ok)
            {
                return Unlisted(
                    written, kustomization, command, fallback,
                    $"kustomize would not add it: {result.Complaint}");
            }
        }
        catch (ToolNotFoundException)
        {
            // Deliberately discovered by running rather than by a readiness check first: one answer,
            // from the thing that would have to do the work anyway.
            return Unlisted(
                written, kustomization, command, fallback,
                "kustomize is not installed, so the kustomization was left untouched. "
                    + $"See {KnownTools.Kustomize.DocumentationUrl}");
        }

        return new SinkResult
        {
            Outcome = SinkOutcome.Registered,
            Path = written.Path,
            Command = command,
            FallbackLine = fallback,
        };
    }

    /// <summary>Written, not listed — say what is missing and exactly what closes the gap.</summary>
    private static SinkResult Unlisted(
        SinkResult written, string kustomization, string command, string fallback, string why)
        => new()
        {
            Outcome = SinkOutcome.NotRegistered,
            Path = written.Path,
            Command = command,
            FallbackLine = fallback,
            Message = $"{written.Path} was written.\n\n{why}\n\n"
                + $"Add this line to {kustomization}, under `resources:`:\n\n{fallback}",
        };
}
