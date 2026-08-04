using System.Net;
using Kontena.Sdk.Tooling;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The tag comes from where <c>github.com/&lt;repo&gt;/releases/latest</c> redirects to, never from
/// <c>api.github.com</c> — that API's anonymous 60-requests-an-hour limit is what this class exists
/// to avoid (KON-311).
/// </summary>
public sealed class GitHubToolReleaseSourceTests
{
    private static readonly ExternalTool Tool = new("widget", "widget", [], [])
    {
        Release = new ToolReleaseSpec("acme/widget", "widget-{os}-{arch}", ".sha256"),
    };

    private static string ExpectedAsset() => Tool.Release!.AssetFor(ToolPlatform.Os, ToolPlatform.Architecture)!;

    /// <summary>Fakes the redirect by handing back a response whose <c>RequestMessage</c> already
    /// carries the URI the real request would have landed on — no need to actually redirect.</summary>
    private sealed class FakeHandler(
        string landedOn, HttpStatusCode latestStatus, string? checksum) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);

            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(latestStatus)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, landedOn),
                });
            }

            return Task.FromResult(checksum is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksum) });
        }
    }

    [Fact]
    public async Task The_tag_is_read_from_where_the_redirect_lands()
    {
        var handler = new FakeHandler(
            "https://github.com/acme/widget/releases/tag/v1.2.3", HttpStatusCode.OK,
            checksum: $"{new string('a', 64)}  {ExpectedAsset()}.sha256");
        var source = new GitHubToolReleaseSource(new HttpClient(handler));

        var download = await source.LatestAsync(Tool);

        Assert.NotNull(download);
        Assert.Equal("v1.2.3", download!.Version);
        Assert.Equal(new string('a', 64), download.Sha256);
        Assert.Equal(
            $"https://github.com/acme/widget/releases/download/v1.2.3/{ExpectedAsset()}", download.Url.ToString());
        Assert.All(handler.Requested, uri => Assert.NotEqual("api.github.com", uri.Host));
    }

    [Fact]
    public async Task A_repository_with_no_releases_is_not_mistaken_for_a_tag_named_latest()
    {
        // No redirect happened: a 404 lands right back on ".../releases/latest" itself, which must
        // not be read as a tag called "latest".
        var handler = new FakeHandler(
            "https://github.com/acme/empty/releases/latest", HttpStatusCode.NotFound, checksum: null);
        var source = new GitHubToolReleaseSource(new HttpClient(handler));

        var download = await source.LatestAsync(Tool with { Release = Tool.Release! with { Repository = "acme/empty" } });

        Assert.Null(download);
    }

    [Fact]
    public async Task No_checksum_means_no_download_even_with_a_good_tag()
    {
        var handler = new FakeHandler(
            "https://github.com/acme/widget/releases/tag/v1.2.3", HttpStatusCode.OK, checksum: null);
        var source = new GitHubToolReleaseSource(new HttpClient(handler));

        Assert.Null(await source.LatestAsync(Tool));
    }
}
