namespace Kontena.Sdk.Tooling;

/// <summary>
/// Where a tool's official binaries come from, for the machines where Kontena has to fetch them
/// itself. Only tools that publish a per-file checksum belong here — without one there is nothing to
/// verify against, and an unverifiable download is not an option Kontena offers.
/// </summary>
/// <remarks>
/// One subtype per publisher rather than one shape with a free-text location field: kind's releases
/// live at a GitHub tag and kubectl's at a versioned path on <c>dl.k8s.io</c>, and those are not the
/// same thing said differently. <see cref="ToolReleaseSources"/> reads the subtype to pick the source
/// that knows how to talk to that publisher.
/// </remarks>
public abstract record ToolReleaseSpec;

/// <summary>
/// A tool published as a GitHub release asset — kind, minikube, k0sctl.
/// </summary>
/// <param name="Repository">GitHub <c>owner/name</c> holding the releases.</param>
/// <param name="AssetPattern">Asset file name with <c>{os}</c> and <c>{arch}</c> placeholders.</param>
/// <param name="ChecksumSuffix">Appended to the asset name to get its checksum file — publishers
/// disagree here: kind uses <c>.sha256sum</c>, minikube <c>.sha256</c>.</param>
/// <param name="ExeOnWindows">Whether the Windows asset carries an <c>.exe</c> suffix. They disagree
/// about this too, and guessing produces a 404 rather than a wrong file.</param>
public sealed record GitHubReleaseSpec(
    string Repository,
    string AssetPattern,
    string ChecksumSuffix,
    bool ExeOnWindows = false) : ToolReleaseSpec
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

/// <summary>
/// A tool published on <c>dl.k8s.io</c>, the Kubernetes project's own download host: kubectl and the
/// rest of the release binaries. The layout is fixed — a channel file names the version, and the
/// binary sits at a path built from it — so there is nothing per-tool to configure at all.
/// </summary>
public sealed record KubernetesReleaseSpec : ToolReleaseSpec;
