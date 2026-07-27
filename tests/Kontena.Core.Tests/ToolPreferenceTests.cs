using System.Security.Cryptography;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Handing a tool to Kontena so its copy wins over a system install (KON-153).
/// <para>
/// The point of these is the precedence, and that it is the same on both sides of the seam: what the
/// settings page reports as being used has to be what actually runs, or the page is lying about the
/// thing it exists to explain.
/// </para>
/// </summary>
public class ToolPreferenceTests
{
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    /// <summary>Put a verified copy in the store, as a download would.</summary>
    private static async Task<string> SeedAsync(ManagedToolStore store, ExternalTool tool)
    {
        var payload = "#!/bin/sh\n"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        var download = new ToolDownload(tool, "v0.32.0", new Uri("https://example.invalid/x"), digest);

        using var content = new MemoryStream(payload);
        return await store.AcceptAsync(download, content);
    }

    [Fact]
    public void Nothing_is_preferred_until_someone_says_so()
    {
        // The default has to be "leave their install alone". Anything else is Kontena deciding to run
        // a different binary than the one on PATH without being asked.
        Assert.False(EmptyStore().IsPreferred(KnownTools.Kind));
    }

    [Fact]
    public async Task A_preferred_copy_is_named_outright_so_it_beats_PATH()
    {
        var store = EmptyStore();
        var path = await SeedAsync(store, KnownTools.Kind);
        store.SetPreferred(KnownTools.Kind, true);

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var resolved = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, store);

        // Not an extra search path: ToolLocator searches PATH first on purpose, so a directory hint
        // would lose to the system install every time. An absolute executable is an answer.
        Assert.Equal(path, resolved.Executable);
    }

    [Fact]
    public async Task Without_the_preference_the_system_install_still_wins()
    {
        var store = EmptyStore();
        await SeedAsync(store, KnownTools.Kind);

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var resolved = await ManagedTools.ResolveAsync(KnownTools.Kind, runner, store);

        Assert.Same(KnownTools.Kind, resolved);
    }

    [Fact]
    public async Task The_readiness_check_agrees_with_what_would_run()
    {
        var store = EmptyStore();
        await SeedAsync(store, KnownTools.Kind);
        store.SetPreferred(KnownTools.Kind, true);

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var readiness = await new ToolReadinessCheck(runner, store).CheckAsync(KnownTools.Kind);

        Assert.True(readiness.Managed);
        Assert.True(readiness.Preferred);
        Assert.Equal("v0.32.0", readiness.Version);
    }

    [Fact]
    public async Task A_preference_with_no_copy_behind_it_falls_back_rather_than_reporting_nothing()
    {
        var store = EmptyStore();
        store.SetPreferred(KnownTools.Kind, true);

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        var readiness = await new ToolReadinessCheck(runner, store).CheckAsync(KnownTools.Kind);

        // The preference says which one wins where there is a choice; it is not a promise to refuse
        // the other one. Reporting "not installed" here would be Kontena's bookkeeping shown as the
        // user's problem.
        Assert.Equal(ToolState.Ready, readiness.State);
        Assert.False(readiness.Managed);
    }

    [Fact]
    public async Task Removing_the_copy_takes_the_preference_with_it()
    {
        var store = EmptyStore();
        await SeedAsync(store, KnownTools.Kind);
        store.SetPreferred(KnownTools.Kind, true);

        store.Remove(KnownTools.Kind);

        // A marker left pointing at nothing would make the next resolve prefer a file that is gone.
        Assert.False(store.IsPreferred(KnownTools.Kind));

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");
        Assert.Same(KnownTools.Kind, await ManagedTools.ResolveAsync(KnownTools.Kind, runner, store));
    }

    [Fact]
    public async Task Giving_it_back_restores_the_system_install()
    {
        var store = EmptyStore();
        await SeedAsync(store, KnownTools.Kind);
        store.SetPreferred(KnownTools.Kind, true);
        store.SetPreferred(KnownTools.Kind, false);

        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0");

        Assert.Same(KnownTools.Kind, await ManagedTools.ResolveAsync(KnownTools.Kind, runner, store));

        // And the copy is still there — this was never a delete.
        Assert.NotNull(store.Record(KnownTools.Kind));
    }
}
