using System.Net.Http;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Reads the newest release straight from the publisher's GitHub releases: the tag, the binary and
/// its checksum, all from plain <c>github.com</c> URLs.
/// </summary>
/// <remarks>
/// Deliberately never touches <c>api.github.com</c>, which anonymously allows only 60 requests per
/// hour per IP — a limit a person opening a settings page runs into for real during development.
/// <c>github.com/&lt;repo&gt;/releases/latest</c> redirects to <c>/releases/tag/&lt;tag&gt;</c>, and
/// that redirect is enough to read the tag; the asset and checksum requests were already plain URLs
/// and were never the problem (KON-311).
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

        if (await LatestTagAsync(spec.Repository, ct) is not { Length: > 0 } tag)
            return null;

        var baseUrl = $"https://github.com/{spec.Repository}/releases/download/{tag}";
        var digest = await ReadChecksumAsync($"{baseUrl}/{asset}{spec.ChecksumSuffix}", ct);

        // No checksum, no download. The offer only exists because the publisher makes verification
        // possible; without it the honest answer is the documentation link.
        return digest is null ? null : new ToolDownload(tool, tag, new Uri($"{baseUrl}/{asset}"), digest);
    }

    /// <summary>
    /// The newest tag, read off the redirect <c>github.com/&lt;repo&gt;/releases/latest</c> gives to
    /// <c>/releases/tag/&lt;tag&gt;</c> — the last path segment of where the request actually landed,
    /// not a body to parse.
    /// </summary>
    private async ValueTask<string?> LatestTagAsync(string repository, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://github.com/{repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);

            // A repository with no releases, or that does not exist, answers "latest" with a 404
            // rather than a redirect — landed-on URI still ends in ".../latest" then, which is not a
            // tag. Only a genuine "/releases/tag/<tag>" landing counts.
            if (!response.IsSuccessStatusCode)
                return null;

            var landedOn = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            const string marker = "/releases/tag/";

            return landedOn.IndexOf(marker, StringComparison.Ordinal) is >= 0 and var i
                ? landedOn[(i + marker.Length)..].TrimEnd('/')
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
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

        // Saying who we are is the polite half of using someone else's bandwidth, API or not.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Name);
        return http;
    }
}
