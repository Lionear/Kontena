using System.Net.Http;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// The two things every release source does the same way: fetch a small text file, and read a
/// checksum out of one. Shared so a second publisher cannot end up parsing digests slightly
/// differently from the first.
/// </summary>
internal static class ToolReleaseHttp
{
    internal static HttpClient Default()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Saying who we are is the polite half of using someone else's bandwidth, API or not.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Name);
        return http;
    }

    /// <summary>
    /// The body, or null when it could not be fetched. A publisher that skipped this architecture
    /// answers 404, and that is an answer — "nothing to offer here" — not a failure to report.
    /// </summary>
    internal static async ValueTask<string?> TextAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            return await http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// A checksum file is <c>&lt;hex&gt;  &lt;filename&gt;</c>, or sometimes just the hex — kubectl
    /// publishes the bare digest, kind the pair. Take the first 64-character hex word and ignore the
    /// rest.
    /// </summary>
    internal static async ValueTask<string?> ChecksumAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (await TextAsync(http, url, ct) is not { } text)
            return null;

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (word.Length == 64 && word.All(Uri.IsHexDigit))
                return word.ToLowerInvariant();

        return null;
    }
}
