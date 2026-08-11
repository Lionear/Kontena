using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk;

namespace Kontena.Core.Versioning;

/// <summary>
/// Reads a product's release calendar from <a href="https://endoflife.date">endoflife.date</a>, which
/// tracks publishers' own support announcements (KON-370).
/// </summary>
/// <remarks>
/// <para>
/// Someone else's calendar rather than a table of ours, because Kontena is not the vendor of Docker,
/// containerd, Podman or Kubernetes and should not be the thing that decides when their releases stop
/// being supported. A list we kept by hand would also be wrong the moment a release ships.
/// </para>
/// <para>
/// The whole product document is fetched — every cycle, a few kilobytes — and the comparison happens on
/// this machine. Asking a service about one specific version would tell it which versions run here,
/// which is exactly the traffic a desktop tool should not generate.
/// </para>
/// </remarks>
public sealed class EndOfLifeCalendar(HttpClient? http = null) : IReleaseCalendar
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = http ?? Default();

    public async ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(
        string product, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://endoflife.date/api/v1/products/{product}", ct);

            // A product nobody tracks answers 404. That is an answer, and the answer is "nothing".
            if (!response.IsSuccessStatusCode)
                return null;

            var document = await response.Content.ReadFromJsonAsync<Document>(Options, ct);

            return document?.Result?.Releases is { Count: > 0 } releases
                ? [.. releases.Select(r => new ReleaseCycle(r.Name, r.IsMaintained, r.EolFrom, r.Latest?.Name))]
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or NotSupportedException)
        {
            // Offline, or an error page where a document was expected. Neither is worth a crash, and
            // neither is news: the caller shows what it knew before, or nothing.
            return null;
        }
    }

    private static HttpClient Default()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Saying who we are is the polite half of using someone else's bandwidth.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Name);
        return http;
    }

    private sealed record Document([property: JsonPropertyName("result")] Product? Result);

    private sealed record Product([property: JsonPropertyName("releases")] IReadOnlyList<Release>? Releases);

    private sealed record Release(string Name, bool IsMaintained, DateOnly? EolFrom, Named? Latest);

    private sealed record Named(string Name);
}
