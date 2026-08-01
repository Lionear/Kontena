using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Core.Tests;

/// <summary>Working out what state a tool is in, and what to offer about it (KON-109).</summary>
public sealed class ToolReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kontena-ready-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ToolReadinessCheck Subject(FakeToolRunner runner) => new(runner, new ManagedToolStore(_root));

    [Fact]
    public async Task A_tool_that_is_not_there_is_missing_and_comes_with_a_hint()
    {
        var check = Subject(new FakeToolRunner());

        var readiness = await check.CheckAsync(KnownTools.Kind);

        Assert.Equal(ToolState.Missing, readiness.State);
        Assert.NotNull(readiness.Hint);
        Assert.False(readiness.Usable);
    }

    [Fact]
    public async Task A_current_tool_is_ready()
    {
        var check = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.31.0 go1.25.5 linux/amd64"));

        var readiness = await check.CheckAsync(KnownTools.Kind);

        Assert.Equal(ToolState.Ready, readiness.State);
        Assert.True(readiness.Usable);
        Assert.False(readiness.Managed);

        // Nothing to offer: an install hint next to a working tool is noise.
        Assert.Null(readiness.Hint);
    }

    [Fact]
    public async Task An_old_tool_is_usable_but_flagged()
    {
        // kind 0.17 predates the config handling Kontena writes. That is a warning, not a refusal —
        // it is the user's machine and most of it still works.
        var check = Subject(new FakeToolRunner().Install(KnownTools.Kind, "kind v0.17.0 go1.19 linux/amd64"));

        var readiness = await check.CheckAsync(KnownTools.Kind);

        Assert.Equal(ToolState.Outdated, readiness.State);
        Assert.True(readiness.Usable);
        Assert.NotNull(readiness.Hint);
    }

    [Fact]
    public async Task A_tool_that_will_not_say_its_version_is_unusable_not_missing()
    {
        // "Install it" is the wrong advice for a binary that is already there and broken.
        var check = Subject(new FakeToolRunner().InstallBroken(KnownTools.Kind));

        var readiness = await check.CheckAsync(KnownTools.Kind);

        Assert.Equal(ToolState.Unusable, readiness.State);
        Assert.False(readiness.Usable);
        Assert.NotNull(readiness.Path);
    }

    [Fact]
    public async Task Only_tools_that_publish_checksums_can_be_downloaded()
    {
        var check = Subject(new FakeToolRunner());

        var kind = await check.CheckAsync(KnownTools.Kind);
        var kubectl = await check.CheckAsync(KnownTools.Kubectl);

        // kind ships per-file checksums; kubectl has no release spec here, so there is nothing to
        // verify against and Kontena does not offer to fetch it.
        Assert.True(kind.CanBeDownloaded == ToolPlatform.CanDownload);
        Assert.False(kubectl.CanBeDownloaded);
    }

    [Theory]
    [InlineData("kind v0.31.0 go1.25.5 linux/amd64", "0.20", false)]
    [InlineData("kind v0.17.0", "0.20", true)]
    [InlineData("v1.38.1", "1.30", false)]
    [InlineData("v1.29.0", "1.30", true)]
    [InlineData("v0.20.0", "0.20", false)]
    [InlineData("Client Version: v1.34.9", "1.30", false)]
    public void Versions_compare_on_their_numbers(string version, string minimum, bool older)
        => Assert.Equal(older, ToolReadinessCheck.IsOlder(version, minimum));

    [Fact]
    public void An_unreadable_version_counts_as_new_enough()
    {
        // Refusing to work because we could not parse a string is our problem presented as the
        // user's. Whatever it is, it answered — that is more than a missing tool does.
        Assert.False(ToolReadinessCheck.IsOlder("built from source", "0.20"));
        Assert.False(ToolReadinessCheck.IsOlder("", "0.20"));
    }

    [Theory]
    [InlineData("linux", "amd64", "kind-linux-amd64")]
    [InlineData("darwin", "arm64", "kind-darwin-arm64")]
    [InlineData("windows", "amd64", "kind-windows-amd64")]
    public void Asset_names_follow_the_publisher(string os, string arch, string expected)
        => Assert.Equal(expected, KnownTools.Kind.Release!.AssetFor(os, arch));

    [Fact]
    public void Minikube_windows_carries_an_exe_suffix_and_kind_does_not()
    {
        // They genuinely disagree, and guessing gives a 404 rather than a wrong file.
        Assert.Equal("minikube-windows-amd64.exe", KnownTools.Minikube.Release!.AssetFor("windows", "amd64"));
        Assert.Equal("kind-windows-amd64", KnownTools.Kind.Release!.AssetFor("windows", "amd64"));
    }

    [Fact]
    public void An_architecture_nobody_publishes_for_has_no_asset()
        => Assert.Null(KnownTools.Kind.Release!.AssetFor("linux", null));

    [Fact]
    public void Only_tools_Kontena_drives_have_a_release_spec()
    {
        // Downloading executables is not a habit to spread: helm, kustomize and podman come from a
        // package manager or not at all.
        Assert.NotNull(KnownTools.Kind.Release);
        Assert.NotNull(KnownTools.Minikube.Release);
        Assert.Null(KnownTools.Helm.Release);
        Assert.Null(KnownTools.Podman.Release);
        Assert.Null(KnownTools.Kubectl.Release);
    }
}
