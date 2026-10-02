using System.Net.Http;
using Kontena.Core.Orchestration.Rendering;
using Kontena.Sdk;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Core.Orchestration.Provisioning;

/// <summary>
/// Turns the <see cref="ClusterManifest"/>s a provisioner asks for after a create into bundles the
/// declarative core can apply (KON-465).
/// <para>
/// This is the whole of the "post-create apply" mechanism that did not exist before: a provisioner names
/// what it wants, this fetches or renders it, and <c>IClusterEngine.ApplyAsync</c> — the same server-side
/// apply the manifest editor uses (KON-86) — puts it on the cluster. Nothing here knows about CNIs, so
/// an add-on that arrives later needs no second path.
/// </para>
/// </summary>
public static class ClusterManifests
{
    /// <summary>
    /// One shared client. Two minutes rather than the usual thirty seconds: an install manifest is a few
    /// hundred kilobytes from a project's own CDN, and a slow connection is not a failure.
    /// </summary>
    private static readonly HttpClient Http = Client();

    /// <summary>
    /// The bundle to apply. Throws with a sentence worth showing rather than returning an empty bundle:
    /// a CNI that silently did not arrive leaves a cluster whose nodes never come Ready, and "nothing
    /// happened" is the one answer that explains none of it.
    /// </summary>
    /// <exception cref="InvalidOperationException">It could not be fetched or rendered.</exception>
    public static async ValueTask<ManifestBundle> ResolveAsync(
        ClusterManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var yaml = manifest.HelmRelease is { Length: > 0 } release
            ? await RenderAsync(manifest, release, ct)
            : await FetchAsync(manifest, ct);

        return new ManifestBundle
        {
            Yaml = yaml,
            Source = manifest.DisplayName,
            Namespace = manifest.Namespace,
        };
    }

    /// <summary>A plain install manifest, straight off the URL it is pinned to.</summary>
    private static async ValueTask<string> FetchAsync(ClusterManifest manifest, CancellationToken ct)
    {
        string yaml;
        try
        {
            yaml = await Http.GetStringAsync(manifest.Url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"{manifest.DisplayName} could not be downloaded from {manifest.Url}: {ex.Message}", ex);
        }

        return yaml.Length > 0
            ? yaml
            : throw new InvalidOperationException($"{manifest.Url} answered, but with nothing in it.");
    }

    /// <summary>
    /// A chart, rendered locally by the renderer the Helm screens already use (KON-89). helm accepts a
    /// chart URL outright, so there is no repository to add and nothing to unpack first.
    /// </summary>
    private static async ValueTask<string> RenderAsync(
        ClusterManifest manifest, string release, CancellationToken ct)
    {
        var result = await new HelmRenderer().RenderAsync(
            new HelmRequest
            {
                Chart = manifest.Url,
                ReleaseName = release,
                Namespace = manifest.Namespace,
                Sets = manifest.HelmValues,

                // Nothing to lint: the chart is a URL, not a directory being worked on.
                Lint = false,
            },
            ct);

        if (result.Ok)
            return result.Yaml;

        var why = result.Diagnostics
            .Where(d => d.Severity == RenderSeverity.Error)
            .Select(d => d.Message)
            .DefaultIfEmpty("the render produced nothing")
            .First();

        throw new InvalidOperationException($"{manifest.DisplayName} could not be rendered: {why}");
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Name);
        return http;
    }
}
