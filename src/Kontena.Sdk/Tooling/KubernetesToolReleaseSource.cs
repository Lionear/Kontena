using System.Net.Http;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Reads the newest release from <c>dl.k8s.io</c>, the Kubernetes project's own download host: the
/// version from <c>release/stable.txt</c>, the binary from the versioned path underneath it, and the
/// digest from the <c>.sha256</c> sitting beside that binary.
/// </summary>
/// <remarks>
/// kubectl is not published as a GitHub release asset, which is why this exists next to
/// <see cref="GitHubToolReleaseSource"/> rather than as another pattern inside it (KON-256).
/// <para>
/// Only <c>dl.k8s.io</c>, deliberately: the older <c>storage.googleapis.com/kubernetes-release/</c>
/// mirror 404s for recent versions, so falling back to it would turn a working install into a
/// confusing one. <c>stable.txt</c> rather than <c>latest.txt</c> for the same reason — the latter
/// names pre-releases, which nobody asked this page for.
/// </para>
/// <para>
/// Each binary also has a cosign <c>.sig</c> and <c>.cert</c> beside it, which is more than the other
/// publishers offer. Kontena verifies the digest only, as it does everywhere else; the signatures are
/// noted here so the next person knows they are there.
/// </para>
/// </remarks>
public sealed class KubernetesToolReleaseSource(HttpClient? http = null) : IToolReleaseSource
{
    private const string Root = "https://dl.k8s.io/release";

    private readonly HttpClient _http = http ?? ToolReleaseHttp.Default();

    public async ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Release is not KubernetesReleaseSpec)
            return null;

        if (ToolPlatform.Architecture is not { } arch)
            return null;

        // A version, or something that is not one: an error page or a captive portal answers 200 with
        // prose, and building a download URL out of that would fetch a 404 and blame the network.
        if (await ToolReleaseHttp.TextAsync(_http, $"{Root}/stable.txt", ct) is not { } text)
            return null;

        var version = text.Trim();
        if (version.Length is 0 or > 32 || !version.StartsWith('v') || version.Any(char.IsWhiteSpace))
            return null;

        // The Windows binary is kubectl.exe and its checksum file is kubectl.exe.sha256 — the suffix
        // is part of the name here, not appended after it.
        var binary = OperatingSystem.IsWindows() ? $"{tool.Executable}.exe" : tool.Executable;
        var url = $"{Root}/{version}/bin/{ToolPlatform.Os}/{arch}/{binary}";

        // No checksum, no download. The offer only exists because the publisher makes verification
        // possible; without it the honest answer is the documentation link.
        return await ToolReleaseHttp.ChecksumAsync(_http, $"{url}.sha256", ct) is { } digest
            ? new ToolDownload(tool, version, new Uri(url), digest)
            : null;
    }
}
