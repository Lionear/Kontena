using Kontena.App.Services;
using Kontena.Core.Models;
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
}
