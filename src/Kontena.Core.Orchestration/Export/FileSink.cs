using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration.Export;

/// <summary>
/// Writes a bundle to <c>&lt;directory&gt;/&lt;name&gt;.yaml</c>, where the name comes from
/// <see cref="ManifestBundle.Source"/> (KON-211).
/// <para>
/// The plain half of exporting: no tool, no index, just the file — so the two things that have to be
/// right, which path it lands on and whether it may replace something, are decided once here and
/// reused by every sink that writes a file first, <see cref="KustomizeSink"/> included.
/// </para>
/// </summary>
public sealed class FileSink(string directory) : IManifestSink
{
    /// <summary>Where files land. Created on the first write when it is not there yet.</summary>
    public string Directory { get; } = directory;

    /// <summary>
    /// Whether an existing file may be replaced. False by default, and deliberately: the target is
    /// somebody's repository, where a file of the same name is more likely to be theirs than a stale
    /// copy of ours, and a lost hand-edit is not something an undo button can reach. Writing the same
    /// bytes again is not a replacement and happens either way — the file, and its timestamp, are
    /// left alone, so re-exporting something unchanged is not an error and produces no git noise.
    /// </summary>
    public bool Overwrite { get; init; }

    public async ValueTask<SinkResult> WriteAsync(ManifestBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var name = SinkPath.FileName(bundle.Source);
        if (name.Length == 0)
        {
            return SinkResult.Refused(
                "This bundle has no source to name a file after. Give it a Source — the rule's name, "
                + "or whatever it should be called on disk.");
        }

        if (bundle.Yaml.AsSpan().Trim().IsEmpty)
            return SinkResult.Refused("There is nothing to write: the bundle holds no YAML.");

        try
        {
            // The name carries no separator by construction, so this cannot leave the directory.
            var target = SinkPath.Real(Directory);
            var path = System.IO.Path.Combine(target, name + ".yaml");

            // Exactly one trailing newline: the file is going into a repository, where a missing one
            // is a diff on the last line every time somebody else's editor adds it.
            var yaml = bundle.Yaml.TrimEnd('\n') + "\n";

            if (File.Exists(path))
            {
                // Writing through a symlink means writing somewhere this sink never agreed to. Repos
                // do carry links to shared manifests, so this is a refusal rather than a warning —
                // and replacing the link itself is not what "overwrite this file" asked for either.
                if (new FileInfo(path).LinkTarget is not null)
                {
                    return SinkResult.Refused(
                        $"'{path}' is a symbolic link. Kontena will not write through one — "
                        + "write to the file it points at, or remove the link.",
                        path);
                }

                if (string.Equals(await File.ReadAllTextAsync(path, ct), yaml, StringComparison.Ordinal))
                    return Wrote(path);

                if (!Overwrite)
                {
                    return SinkResult.Refused(
                        $"'{path}' already exists and holds something else. Move it aside, or export "
                        + "under another name.",
                        path);
                }
            }

            System.IO.Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(path, yaml, ct);

            return Wrote(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SinkResult { Outcome = SinkOutcome.Failed, Message = ex.Message };
        }
    }

    private static SinkResult Wrote(string path) => new() { Outcome = SinkOutcome.Written, Path = path };
}
