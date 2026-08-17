using System.Net.Http;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Every publisher Kontena knows how to fetch from, behind one <see cref="IToolReleaseSource"/>: the
/// tool's <see cref="ExternalTool.Release"/> says whose it is, and this hands it to the source that
/// speaks that publisher's layout (KON-256).
/// </summary>
/// <remarks>
/// A tool with no spec — or one from a publisher no source here handles — answers null, which is the
/// same "nothing to offer" a lookup that failed gives. Callers already treat that as "show the
/// documentation link instead".
/// </remarks>
public sealed class ToolReleaseSources(HttpClient? http = null) : IToolReleaseSource
{
    private readonly GitHubToolReleaseSource _gitHub = new(http);
    private readonly KubernetesToolReleaseSource _kubernetes = new(http);

    public ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return tool.Release switch
        {
            GitHubReleaseSpec => _gitHub.LatestAsync(tool, ct),
            KubernetesReleaseSpec => _kubernetes.LatestAsync(tool, ct),
            _ => ValueTask.FromResult<ToolDownload?>(null),
        };
    }
}
