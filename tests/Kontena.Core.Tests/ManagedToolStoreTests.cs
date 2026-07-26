using System.Security.Cryptography;
using System.Text;
using Kontena.Core.Tooling;

namespace Kontena.Core.Tests;

/// <summary>
/// The directory Kontena keeps its own copies of external tools in (KON-109).
/// <para>
/// This is the one place in the app where bytes off the network become something that gets executed,
/// so the tests are mostly about refusing: a wrong digest, a file changed after the fact, a download
/// that never finished. Each of those has to end with nothing runnable on disk.
/// </para>
/// </summary>
public sealed class ManagedToolStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-tools-{Guid.NewGuid():N}");
    private readonly ManagedToolStore _store;

    private static readonly ExternalTool Tool = new("probe", "kontena-probe", ["--version"], []);

    public ManagedToolStoreTests() => _store = new ManagedToolStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static (byte[] Bytes, string Sha256) Payload(string content = "#!/bin/sh\necho hello\n")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static ToolDownload Download(string sha, string version = "v1.2.3") =>
        new(Tool, version, new Uri("https://example.invalid/probe"), sha);

    [Fact]
    public async Task Accepts_a_download_that_matches_its_checksum()
    {
        var (bytes, sha) = Payload();

        using var content = new MemoryStream(bytes);
        var path = await _store.AcceptAsync(Download(sha), content);

        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        var record = _store.Record(Tool);
        Assert.NotNull(record);
        Assert.Equal(sha, record.Sha256);
        Assert.Equal("v1.2.3", record.Version);
    }

    [Fact]
    public async Task Refuses_a_download_whose_checksum_is_wrong_and_leaves_nothing_behind()
    {
        var (bytes, _) = Payload();

        using var content = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAsync<ToolVerificationException>(
            async () => await _store.AcceptAsync(Download(new string('0', 64)), content));

        Assert.Equal(Tool.Name, ex.Tool);
        Assert.False(File.Exists(_store.PathFor(Tool)));

        // Not even a partial file: an interrupted or tampered download must not leave something a
        // later "is it there?" would answer yes to.
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Verifies_again_before_use()
    {
        var (bytes, sha) = Payload();
        using var content = new MemoryStream(bytes);
        var path = await _store.AcceptAsync(Download(sha), content);

        Assert.Equal(path, await _store.VerifiedPathAsync(Tool));

        // Something changed the file after we verified it — which is exactly the case checking only
        // at download time would miss.
        await File.AppendAllTextAsync(path, "tampered");

        Assert.Null(await _store.VerifiedPathAsync(Tool));
    }

    [Fact]
    public async Task Makes_the_copy_executable()
    {
        // Unix file modes do not apply on Windows, where being on disk is enough to run. Written as a
        // return rather than a skip attribute so this project keeps its dependency list short.
        if (OperatingSystem.IsWindows())
            return;

        var (bytes, sha) = Payload();
        using var content = new MemoryStream(bytes);
        var path = await _store.AcceptAsync(Download(sha), content);

        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Nothing_is_reported_when_there_is_no_copy()
    {
        Assert.Null(_store.Record(Tool));
        Assert.Null(await _store.VerifiedPathAsync(Tool));
    }

    [Fact]
    public async Task Removing_takes_the_binary_and_its_record()
    {
        var (bytes, sha) = Payload();
        using var content = new MemoryStream(bytes);
        await _store.AcceptAsync(Download(sha), content);

        _store.Remove(Tool);

        Assert.Null(_store.Record(Tool));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task A_second_download_replaces_the_first()
    {
        var (first, firstSha) = Payload("one");
        using (var content = new MemoryStream(first))
            await _store.AcceptAsync(Download(firstSha, "v1"), content);

        var (second, secondSha) = Payload("two");
        using (var content = new MemoryStream(second))
            await _store.AcceptAsync(Download(secondSha, "v2"), content);

        var record = _store.Record(Tool);
        Assert.Equal("v2", record!.Version);
        Assert.Equal(secondSha, record.Sha256);
        Assert.NotNull(await _store.VerifiedPathAsync(Tool));
    }

    [Fact]
    public void The_default_root_is_beside_the_settings_not_in_a_temp_folder()
    {
        // A temp directory gets swept, and re-downloading a tool because the OS tidied up is the kind
        // of mystery nobody enjoys.
        var root = ManagedToolStore.DefaultRoot();

        Assert.Contains("Kontena", root, StringComparison.Ordinal);
        Assert.EndsWith("tools", root, StringComparison.Ordinal);
    }
}
