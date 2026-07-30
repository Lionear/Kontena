using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Reads the newest release straight from the publisher's GitHub releases: the tag from the API, the
/// binary and its checksum from the release assets.
/// </summary>
/// <remarks>
/// Unauthenticated, so it is subject to GitHub's anonymous rate limit. That is acceptable for
/// something a person triggers by opening a settings page, and the alternative — asking users for a
/// token to install a tool — is worse than the occasional "could not check right now".
/// </remarks>
public sealed class GitHubToolReleaseSource(HttpClient? http = null) : IToolReleaseSource
{
    private readonly HttpClient _http = http ?? Default();

    public async ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default)
    {
        if (tool.Release is not { } spec)
            return null;

        if (spec.AssetFor(ToolPlatform.Os, ToolPlatform.Architecture) is not { } asset)
            return null;

        var release = await _http.GetFromJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{spec.Repository}/releases/latest", ct);

        if (release?.TagName is not { Length: > 0 } tag)
            return null;

        var baseUrl = $"https://github.com/{spec.Repository}/releases/download/{tag}";
        var digest = await ReadChecksumAsync($"{baseUrl}/{asset}{spec.ChecksumSuffix}", ct);

        // No checksum, no download. The offer only exists because the publisher makes verification
        // possible; without it the honest answer is the documentation link.
        return digest is null ? null : new ToolDownload(tool, tag, new Uri($"{baseUrl}/{asset}"), digest);
    }

    /// <summary>
    /// A checksum file is <c>&lt;hex&gt;  &lt;filename&gt;</c>, or sometimes just the hex. Take the
    /// first 64-character hex word and ignore the rest.
    /// </summary>
    private async ValueTask<string?> ReadChecksumAsync(string url, CancellationToken ct)
    {
        try
        {
            var text = await _http.GetStringAsync(url, ct);

            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (word.Length == 64 && word.All(Uri.IsHexDigit))
                    return word.ToLowerInvariant();

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static HttpClient Default()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // GitHub's API refuses requests without one, and saying who we are is the polite half of
        // using someone else's bandwidth.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Name);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? TagName);
}
