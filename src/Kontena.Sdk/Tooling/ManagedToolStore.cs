using System.Security.Cryptography;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// The one directory Kontena keeps its own copies of external tools in, for machines where no package
/// manager can supply them.
/// </summary>
/// <remarks>
/// <para>
/// Everything about this type is a boundary. A managed copy lives in exactly one place, is verified
/// against the publisher's digest when it arrives <em>and</em> before every run, and is never put on
/// PATH — nothing outside Kontena starts behaving differently because Kontena downloaded something.
/// </para>
/// <para>
/// Re-hashing before each run costs a few milliseconds for kind and about a fifth of a second for
/// minikube, against operations that take minutes. That is a cheap way to know the file being run is
/// still the file that was verified.
/// </para>
/// </remarks>
public sealed class ManagedToolStore
{
    private readonly string _root;

    /// <param name="root">Where to keep the copies. Defaults to Kontena's own data directory.</param>
    public ManagedToolStore(string? root = null) => _root = root ?? DefaultRoot();

    /// <summary>The directory itself, so a settings page can show where things went.</summary>
    public string Root => _root;

    /// <summary>
    /// Under the platform's application-data directory, beside the settings — not in a temp folder
    /// that gets swept, and not anywhere a shell would find it by accident.
    /// </summary>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "Kontena", "tools");

    /// <summary>The path a managed copy would live at, whether or not it is there.</summary>
    public string PathFor(ExternalTool tool) => Path.Combine(
        _root, OperatingSystem.IsWindows() ? $"{tool.Executable}.exe" : tool.Executable);

    /// <summary>The recorded digest and version for a managed copy, or null when there is none.</summary>
    public ManagedToolRecord? Record(ExternalTool tool)
    {
        var manifest = ManifestPath(tool);
        if (!File.Exists(manifest) || !File.Exists(PathFor(tool)))
            return null;

        var lines = File.ReadAllLines(manifest);
        if (lines.Length < 2)
            return null;

        return new ManagedToolRecord(tool, PathFor(tool), lines[0].Trim(), lines[1].Trim());
    }

    /// <summary>
    /// The managed copy's path if it is present <em>and</em> still hashes to what was recorded;
    /// otherwise null. A copy that no longer matches is not run — see the remarks on this type.
    /// </summary>
    public async ValueTask<string?> VerifiedPathAsync(ExternalTool tool, CancellationToken ct = default)
    {
        if (Record(tool) is not { } record)
            return null;

        var actual = await Sha256Async(record.Path, ct);
        return string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase) ? record.Path : null;
    }

    /// <summary>
    /// Write a downloaded tool into the store, verifying it first. The file only appears at its final
    /// name once the digest matches, so an interrupted or tampered download cannot leave something
    /// runnable behind.
    /// </summary>
    /// <exception cref="ToolVerificationException">The bytes do not match the published digest.</exception>
    public async ValueTask<string> AcceptAsync(
        ToolDownload download, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);

        var destination = PathFor(download.Tool);
        var staging = destination + ".incoming";

        try
        {
            await using (var file = File.Create(staging))
                await content.CopyToAsync(file, ct);

            var actual = await Sha256Async(staging, ct);
            if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ToolVerificationException(download.Tool.Name, download.Sha256, actual);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    staging,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            File.Move(staging, destination, overwrite: true);
            await File.WriteAllTextAsync(
                ManifestPath(download.Tool),
                $"{download.Sha256}{Environment.NewLine}{download.Version}{Environment.NewLine}", ct);

            return destination;
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    /// <summary>
    /// Whether Kontena's copy should be used even though a system install exists (KON-153).
    /// <para>
    /// False by default, and that default is deliberate: someone who installed kind themselves expects
    /// Kontena to run theirs. This is the switch that lets them hand that over — never something
    /// Kontena decides, because quietly running a different binary than the one on PATH is the kind of
    /// surprise that is only ever discovered while debugging something else.
    /// </para>
    /// </summary>
    public bool IsPreferred(ExternalTool tool) => File.Exists(PreferencePath(tool));

    /// <summary>
    /// Say which copy wins for this tool. Kept as a marker file beside the copy rather than in
    /// <c>KontenaSettings</c>, so the code that resolves a tool reads it from the store it already has
    /// and nothing has to carry the preference through a view model to get there.
    /// </summary>
    public void SetPreferred(ExternalTool tool, bool preferred)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var marker = PreferencePath(tool);

        if (!preferred)
        {
            if (File.Exists(marker))
                File.Delete(marker);

            return;
        }

        Directory.CreateDirectory(_root);
        File.WriteAllText(marker, string.Empty);
    }

    /// <summary>Remove a managed copy and its record. Nothing else in the directory is touched.</summary>
    public void Remove(ExternalTool tool)
    {
        // The preference goes with it: a marker pointing at a copy that is no longer there would make
        // the next resolve prefer nothing at all.
        foreach (var path in new[] { PathFor(tool), ManifestPath(tool), PreferencePath(tool) })
            if (File.Exists(path))
                File.Delete(path);
    }

    private string ManifestPath(ExternalTool tool) => PathFor(tool) + ".sha256";

    private string PreferencePath(ExternalTool tool) => PathFor(tool) + ".preferred";

    private static async ValueTask<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, ct);
        return Convert.ToHexStringLower(hash);
    }
}
