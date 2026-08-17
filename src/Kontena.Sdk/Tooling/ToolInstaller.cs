using System.Net.Http;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Installs a tool one of the two ways Kontena offers: by running the machine's own package manager,
/// or — where there is none — by fetching the publisher's release into Kontena's managed directory.
/// </summary>
/// <remarks>
/// The two are not equivalent and the UI should not present them as such. A package-manager install
/// is <em>the user's</em> install: on PATH, updated with everything else, removable the usual way.
/// A managed copy is Kontena's, and Kontena carries it — which is why it is the fallback and not the
/// default.
/// </remarks>
public sealed class ToolInstaller(
    IToolRunner runner,
    IToolReleaseSource? releases = null,
    ManagedToolStore? store = null,
    HttpClient? http = null)
{
    private readonly IToolReleaseSource _releases = releases ?? new ToolReleaseSources();
    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Run the package manager's install command, streaming its output as it goes. The output is the
    /// package manager's, unedited — including any password prompt, which is its own business and
    /// never something Kontena asks on its behalf.
    /// </summary>
    /// <exception cref="ToolFailedException">The package manager exited non-zero.</exception>
    public IAsyncEnumerable<ToolLine> InstallWithPackageManagerAsync(
        InstallHint hint, CancellationToken ct = default)
    {
        var manager = new ExternalTool(hint.Executable, hint.Executable, ["--version"], []);
        return runner.StreamAsync(new ToolInvocation(manager, hint.Arguments), ct);
    }

    /// <summary>What Kontena would fetch, so the UI can name the version before anything happens.</summary>
    public ValueTask<ToolDownload?> FindDownloadAsync(ExternalTool tool, CancellationToken ct = default)
        => _releases.LatestAsync(tool, ct);

    /// <summary>
    /// Fetch a release into the managed directory, verifying it against the publisher's checksum
    /// before it is ever runnable. Reports bytes so a progress bar can mean something.
    /// </summary>
    /// <exception cref="ToolVerificationException">The bytes did not match the published digest.</exception>
    public async ValueTask<string> DownloadAsync(
        ToolDownload download, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var network = await response.Content.ReadAsStreamAsync(ct);
        await using var counted = progress is null ? network : new CountingStream(network, progress);

        return await _store.AcceptAsync(download, counted, ct);
    }

    /// <summary>Reports how much has arrived, without buffering any of it.</summary>
    private sealed class CountingStream(Stream inner, IProgress<long> progress) : Stream
    {
        private long _total;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
                progress.Report(_total += read);

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
                progress.Report(_total += read);

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
