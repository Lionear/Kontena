using Kontena.App.ViewModels;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The tooling page's side of KON-153: noticing a newer release, and handing a tool over so Kontena's
/// copy is the one that runs.
/// </summary>
public sealed class ToolHandoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-handover-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ClusterToolingViewModel Subject(FakeToolRunner runner, FakeToolReleaseSource releases)
        => new(runner, releases, new ManagedToolStore(_root))
        {
            Now = () => new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
            RequestConfirm = request => _ = request.OnConfirm(),
        };

    private static FakeToolRunner WithKind(string version = "kind v0.31.0 go1.25.5 linux/amd64")
        => new FakeToolRunner().Install(KnownTools.Kind, version);

    [Fact]
    public async Task A_newer_release_shows_as_a_line_and_not_as_a_state()
    {
        var page = Subject(WithKind(), new FakeToolReleaseSource().Publish(KnownTools.Kind, "v0.32.0"));

        await page.LoadAsync();
        await page.RefreshUpdatesAsync();

        var kind = page.Tools.First(t => t.Name == "kind");

        Assert.True(kind.HasUpdate);
        Assert.Equal("v0.32.0 is available", kind.UpdateText);

        // Still Ready. A tool one release behind does its job, and colouring it would train people to
        // ignore the states that mean something.
        Assert.True(kind.IsReady);
        Assert.False(kind.IsOutdated);
    }

    [Fact]
    public async Task Up_to_date_says_nothing()
    {
        var page = Subject(WithKind("kind v0.32.0"), new FakeToolReleaseSource().Publish(KnownTools.Kind, "v0.32.0"));

        await page.LoadAsync();
        await page.RefreshUpdatesAsync();

        Assert.False(page.Tools.First(t => t.Name == "kind").HasUpdate);
    }

    [Fact]
    public async Task Handing_a_tool_over_is_offered_only_where_there_is_something_to_hand_over()
    {
        var page = Subject(WithKind(), new FakeToolReleaseSource());
        await page.LoadAsync();

        // kind is installed by the user and Kontena knows where to fetch it: the offer makes sense.
        Assert.True(page.Tools.First(t => t.Name == "kind").CanHandOver);

        // minikube is not installed at all. That is what Install and Download are for — a third verb
        // for the same act is three ways to be unsure.
        Assert.False(page.Tools.First(t => t.Name == "minikube").CanHandOver);
    }

    [Fact]
    public async Task A_fetch_that_fails_leaves_no_preference_behind()
    {
        var store = new ManagedToolStore(_root);
        var runner = WithKind();
        var page = Subject(runner, new FakeToolReleaseSource().Publish(KnownTools.Kind, "v0.32.0"));

        await page.LoadAsync();
        await page.PreferManagedAsync(page.Tools.First(t => t.Name == "kind"));

        // The download reaches a URL this test cannot serve, so the copy does not land — and the
        // preference must not be set for a copy that is not there. Anything else would leave the row
        // claiming Kontena is in charge of something it does not have.
        Assert.False(store.IsPreferred(KnownTools.Kind));
        Assert.NotNull(page.Error);
    }

    [Fact]
    public async Task Giving_it_back_is_offered_once_it_is_handed_over()
    {
        var store = new ManagedToolStore(_root);
        var runner = WithKind();

        // Stand in for a completed download: the store is the boundary, and this is what lands in it.
        await Seed(store);
        store.SetPreferred(KnownTools.Kind, true);

        var page = Subject(runner, new FakeToolReleaseSource());
        await page.LoadAsync();

        var kind = page.Tools.First(t => t.Name == "kind");
        Assert.True(kind.IsKontenaManaged);
        Assert.True(kind.CanUseSystemAgain);
        Assert.False(kind.CanHandOver);
        Assert.Contains("chosen over the system install", kind.Detail, StringComparison.Ordinal);

        await page.PreferSystemAsync(kind);

        Assert.False(store.IsPreferred(KnownTools.Kind));
        Assert.False(page.Tools.First(t => t.Name == "kind").IsKontenaManaged);

        // Giving it back is not a delete: the copy is still there to hand over again.
        Assert.NotNull(store.Record(KnownTools.Kind));
    }

    private static async Task Seed(ManagedToolStore store)
    {
        var payload = "#!/bin/sh\n"u8.ToArray();
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var download = new ToolDownload(
            KnownTools.Kind, "v0.32.0", new Uri("https://example.invalid/kind"), digest);

        using var content = new MemoryStream(payload);
        await store.AcceptAsync(download, content);
    }
}
