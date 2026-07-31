using Kontena.App.Services;

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
}
