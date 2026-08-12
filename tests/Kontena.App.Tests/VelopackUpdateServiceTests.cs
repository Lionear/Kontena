using Kontena.App.Services;
using Kontena.Core.Models;
using Velopack;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// Where each channel's release assets live — a plain <c>github.com</c> URL, never
/// <c>api.github.com</c> (KON-312). The rolling streams' assets sit at a fixed tag the Build workflow
/// always republishes onto, so they need no lookup any more than stable's "latest" does.
/// </summary>
public sealed class VelopackUpdateServiceTests
{
    private const string Repo = "https://github.com/Lionear/Kontena";

    [Fact]
    public void Stable_reads_off_the_latest_release()
    {
        Assert.Equal(
            $"{Repo}/releases/latest/download", VelopackUpdateService.BaseUrlFor(Repo, UpdateChannel.Stable));
    }

    [Fact]
    public void Nightly_reads_off_its_own_rolling_tag()
    {
        Assert.Equal(
            $"{Repo}/releases/download/nightly", VelopackUpdateService.BaseUrlFor(Repo, UpdateChannel.Nightly));
    }

    [Fact]
    public void Preview_reads_off_its_own_rolling_tag()
    {
        Assert.Equal(
            $"{Repo}/releases/download/preview", VelopackUpdateService.BaseUrlFor(Repo, UpdateChannel.Preview));
    }

    /// <summary>
    /// The property the bug rode on (KON-372): the prerelease tag is the first identifier and is
    /// compared as text, so the channel name decides the order. Nightly sorts *below* preview, which is
    /// why switching preview → nightly looked like "you are up to date".
    /// </summary>
    [Fact]
    public void A_nightly_is_semver_below_the_preview_of_the_same_version()
    {
        Assert.True(
            SemanticVersion.Parse("0.4.0-nightly.20260811.3")
            < SemanticVersion.Parse("0.4.0-preview.20260804.1"));
    }

    [Theory]
    [InlineData(UpdateChannel.Preview, UpdateChannel.Nightly)]   // the reported case
    [InlineData(UpdateChannel.Preview, UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Stable, UpdateChannel.Nightly)]
    public void Switching_channel_may_go_to_a_lower_version(UpdateChannel build, UpdateChannel target)
    {
        Assert.True(VelopackUpdateService.OptionsFor(target, build).AllowVersionDowngrade);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Preview)]
    [InlineData(UpdateChannel.Nightly)]
    public void Staying_on_a_channel_never_goes_backwards(UpdateChannel channel)
    {
        // A feed on your own channel that rolls back is the case the default guards against, and that
        // guard stays on: only a switch the user asked for by name may move down.
        Assert.False(VelopackUpdateService.OptionsFor(channel, channel).AllowVersionDowngrade);
        Assert.Equal(ReleaseChannel.ForCurrentPlatform(channel), VelopackUpdateService.OptionsFor(channel, channel).ExplicitChannel);
    }
}
