using Kontena.App.Services;
using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// The version string every surface shows.
/// <para>
/// The regression worth holding is the prerelease part. It used to come from the assembly version,
/// which is four integers and cannot carry one, so a nightly called itself <c>0.3.0</c> — and the
/// update card offered <c>0.3.0 → 0.3.0-nightly.20260731.44</c> to an install already running that
/// exact nightly.
/// </para>
/// </summary>
public sealed class AppVersionTests
{
    [Fact]
    public void A_nightly_keeps_its_prerelease_part()
    {
        var version = AppVersion.From("0.3.0-nightly.20260731.44", new Version(0, 3, 0, 0));

        Assert.Equal("0.3.0-nightly.20260731.44", version);
    }

    [Fact]
    public void The_commit_sourcelink_appends_is_not_part_of_the_version()
    {
        var version = AppVersion.From("0.3.0-nightly.20260731.44+eb6bed0", new Version(0, 3, 0, 0));

        Assert.Equal("0.3.0-nightly.20260731.44", version);
    }

    [Fact]
    public void A_release_build_reads_as_it_was_tagged()
    {
        Assert.Equal("0.3.0", AppVersion.From("0.3.0", new Version(0, 3, 0, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_informational_version_the_numeric_one_answers(string? informational)
    {
        Assert.Equal("0.3.0", AppVersion.From(informational, new Version(0, 3, 0, 0)));
    }

    [Fact]
    public void A_host_with_neither_still_gives_something_showable()
    {
        Assert.Equal("0.0.0", AppVersion.From(null, null));
    }

    /// <summary>
    /// A build the workflow did not make carries no date, and then the line must not keep the
    /// separator that was there to introduce it.
    /// </summary>
    [Fact]
    public void A_build_without_a_date_shows_no_dangling_separator()
    {
        Assert.Null(AppVersion.BuildDate);
        Assert.Equal(string.Empty, new AboutViewModel(new NoKeychain()).BuildDateSuffix);
    }

    private sealed class NoKeychain : ISecretStore
    {
        public bool IsAvailable => false;

        public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask DeleteAsync(string key, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
