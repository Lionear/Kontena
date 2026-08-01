namespace Kontena.Sdk.Tooling;

/// <summary>
/// Where a tool's official binaries come from, for the machines where Kontena has to fetch them
/// itself. Only tools that publish a per-file checksum belong here — without one there is nothing to
/// verify against, and an unverifiable download is not an option Kontena offers.
/// </summary>
/// <param name="Repository">GitHub <c>owner/name</c> holding the releases.</param>
/// <param name="AssetPattern">Asset file name with <c>{os}</c> and <c>{arch}</c> placeholders.</param>
/// <param name="ChecksumSuffix">Appended to the asset name to get its checksum file — publishers
/// disagree here: kind uses <c>.sha256sum</c>, minikube <c>.sha256</c>.</param>
/// <param name="ExeOnWindows">Whether the Windows asset carries an <c>.exe</c> suffix. They disagree
/// about this too, and guessing produces a 404 rather than a wrong file.</param>
public sealed record ToolReleaseSpec(
    string Repository,
    string AssetPattern,
    string ChecksumSuffix,
    bool ExeOnWindows = false)
{
    /// <summary>The asset name for this machine, or null on an architecture nobody publishes for.</summary>
    public string? AssetFor(string os, string? arch)
    {
        if (arch is null)
            return null;

        var name = AssetPattern.Replace("{os}", os, StringComparison.Ordinal)
                               .Replace("{arch}", arch, StringComparison.Ordinal);

        return os == "windows" && ExeOnWindows ? name + ".exe" : name;
    }
}
