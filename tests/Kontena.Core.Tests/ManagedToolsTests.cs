using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.Core.Tests;

public class ManagedToolsTests
{
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    [Fact]
    public async Task A_system_install_is_used_as_is()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");

        var resolved = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, EmptyStore());

        Assert.Same(KnownTools.Kind, resolved);
    }

    [Fact]
    public async Task Without_the_tool_anywhere_nothing_is_added_to_look_in()
    {
        var resolved = await ManagedTools.ResolveAsync(KnownTools.Kind, new FakeToolRunner(), EmptyStore());

        Assert.Empty(resolved.ExtraSearchPaths);
    }

    [Fact]
    public async Task Kontenas_own_copy_is_made_findable_when_there_is_no_system_install()
    {
        var store = EmptyStore();
        var payload = "#!/bin/sh\n"u8.ToArray();
        var download = new ToolDownload(
            KnownTools.Kind, "v0.31.0", new Uri("https://example.invalid/kind"), Sha256(payload));

        using var content = new MemoryStream(payload);
        var path = await store.AcceptAsync(download, content);

        var resolved = await ManagedTools.ResolveAsync(KnownTools.Kind, new FakeToolRunner(), store);

        Assert.Contains(Path.GetDirectoryName(path)!, resolved.ExtraSearchPaths);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
