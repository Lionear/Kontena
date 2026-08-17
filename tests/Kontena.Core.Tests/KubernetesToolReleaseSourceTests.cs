using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Core.Tests;

/// <summary>
/// kubectl comes from <c>dl.k8s.io</c>, not from a GitHub release (KON-256): the version out of
/// <c>stable.txt</c>, the binary from the versioned path under it, the digest from the <c>.sha256</c>
/// beside it. Nothing here reaches the network — the handler answers for it.
/// </summary>
public sealed class KubernetesToolReleaseSourceTests : IDisposable
{
    private const string Version = "v1.36.3";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-k8s-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>The path this machine's kubectl lives at, so the assertions are not linux-only.</summary>
    private static string ExpectedUrl(string version = Version)
    {
        var binary = OperatingSystem.IsWindows() ? "kubectl.exe" : "kubectl";
        return $"https://dl.k8s.io/release/{version}/bin/{ToolPlatform.Os}/{ToolPlatform.Architecture}/{binary}";
    }

    /// <summary>
    /// Answers the three requests dl.k8s.io would: the channel file, the checksum, the binary itself.
    /// A null checksum or body means "not published", which is a 404 rather than an empty answer.
    /// </summary>
    private sealed class FakeHandler(string? stable, string? checksum, byte[]? binary = null) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);

            HttpContent? body = request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("/stable.txt", StringComparison.Ordinal) => Text(stable),
                var path when path.EndsWith(".sha256", StringComparison.Ordinal) => Text(checksum),
                _ => binary is null ? null : new ByteArrayContent(binary),
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }

        private static StringContent? Text(string? value) => value is null ? null : new StringContent(value);
    }

    [Fact]
    public async Task The_version_comes_from_stable_and_the_binary_from_the_path_under_it()
    {
        var digest = new string('a', 64);
        var handler = new FakeHandler($"{Version}\n", digest);
        var source = new KubernetesToolReleaseSource(new HttpClient(handler));

        var download = await source.LatestAsync(KnownTools.Kubectl);

        Assert.NotNull(download);
        Assert.Equal(Version, download!.Version);
        Assert.Equal(digest, download.Sha256);
        Assert.Equal(ExpectedUrl(), download.Url.ToString());
        Assert.All(handler.Requested, uri => Assert.Equal("dl.k8s.io", uri.Host));
    }

    [Fact]
    public async Task The_digest_is_read_bare_the_way_kubernetes_publishes_it()
    {
        // kubectl's .sha256 is the hex on its own, with no file name after it — kind's has both.
        var digest = new string('b', 64);
        var source = new KubernetesToolReleaseSource(new HttpClient(new FakeHandler(Version, $"{digest}\n")));

        Assert.Equal(digest, (await source.LatestAsync(KnownTools.Kubectl))!.Sha256);
    }

    [Fact]
    public async Task No_checksum_means_no_download_even_with_a_version()
    {
        // The offer only exists because verification is possible. Without a digest there is nothing to
        // check the bytes against, so there is nothing to offer.
        var source = new KubernetesToolReleaseSource(new HttpClient(new FakeHandler(Version, checksum: null)));

        Assert.Null(await source.LatestAsync(KnownTools.Kubectl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>Sign in to the network</html>")]
    [InlineData("1.36.3")]
    public async Task Anything_that_is_not_a_version_is_not_treated_as_one(string? stable)
    {
        // A captive portal answers 200 with prose. Pasting that into a URL would fetch a 404 and
        // report it as the network being down.
        var source = new KubernetesToolReleaseSource(
            new HttpClient(new FakeHandler(stable, new string('a', 64))));

        Assert.Null(await source.LatestAsync(KnownTools.Kubectl));
    }

    [Fact]
    public async Task A_tool_published_somewhere_else_is_not_this_sources_business()
    {
        var source = new KubernetesToolReleaseSource(new HttpClient(new FakeHandler(Version, new string('a', 64))));

        Assert.Null(await source.LatestAsync(KnownTools.Kind));
        Assert.Null(await source.LatestAsync(KnownTools.Helm));
    }

    [Fact]
    public async Task The_dispatcher_sends_each_tool_to_the_publisher_that_has_it()
    {
        // Only dl.k8s.io answers here, so kind resolving to nothing is the proof that it was not sent
        // to this handler — and kubectl resolving is the proof that it was.
        var sources = new ToolReleaseSources(new HttpClient(new FakeHandler(Version, new string('a', 64))));

        Assert.NotNull(await sources.LatestAsync(KnownTools.Kubectl));
        Assert.Null(await sources.LatestAsync(KnownTools.Kind));
        Assert.Null(await sources.LatestAsync(KnownTools.Podman));
    }

    [Fact]
    public async Task A_download_whose_bytes_do_not_match_the_published_digest_fails_loudly()
    {
        // End to end: look the release up, fetch it, refuse it. The store is where the bytes are
        // checked, and nothing runnable is left behind when they are wrong.
        var bytes = Encoding.UTF8.GetBytes("not kubectl");
        var handler = new FakeHandler(Version, new string('c', 64), bytes);
        var store = new ManagedToolStore(_root);
        var installer = new ToolInstaller(
            new FakeToolRunner(),
            new KubernetesToolReleaseSource(new HttpClient(handler)),
            store,
            new HttpClient(handler));

        var download = await installer.FindDownloadAsync(KnownTools.Kubectl);
        Assert.NotNull(download);

        var ex = await Assert.ThrowsAsync<ToolVerificationException>(
            async () => await installer.DownloadAsync(download!));

        Assert.Equal("kubectl", ex.Tool);
        Assert.False(File.Exists(store.PathFor(KnownTools.Kubectl)));
    }

    [Fact]
    public async Task A_download_that_matches_lands_in_the_managed_store()
    {
        var bytes = Encoding.UTF8.GetBytes("#!/bin/sh\necho kubectl\n");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var handler = new FakeHandler(Version, digest, bytes);
        var store = new ManagedToolStore(_root);
        var installer = new ToolInstaller(
            new FakeToolRunner(),
            new KubernetesToolReleaseSource(new HttpClient(handler)),
            store,
            new HttpClient(handler));

        var path = await installer.DownloadAsync((await installer.FindDownloadAsync(KnownTools.Kubectl))!);

        Assert.Equal(store.PathFor(KnownTools.Kubectl), path);
        Assert.Equal(Version, store.Record(KnownTools.Kubectl)!.Version);
        Assert.Equal(path, await store.VerifiedPathAsync(KnownTools.Kubectl));
    }
}
