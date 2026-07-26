using Kontena.Core.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The channel name is a contract between two places that never run together: the packaging step in
/// <c>build.yml</c> writes a feed under this name, and the app asks for it by the same name months
/// later. These pin the shape, because a rename on one side alone is silent — the client simply
/// never finds an update again.
/// </summary>
public class ReleaseChannelTests
{
    private static readonly string[] Platforms = ["win", "linux", "osx"];

    [Theory]
    [InlineData(UpdateChannel.Stable, "win", "win-stable")]
    [InlineData(UpdateChannel.Stable, "linux", "linux-stable")]
    [InlineData(UpdateChannel.Stable, "osx", "osx-stable")]
    [InlineData(UpdateChannel.Nightly, "win", "win-nightly")]
    [InlineData(UpdateChannel.Nightly, "linux", "linux-nightly")]
    [InlineData(UpdateChannel.Nightly, "osx", "osx-nightly")]
    public void Names_a_channel_per_platform_and_stream(UpdateChannel channel, string platform, string expected) =>
        Assert.Equal(expected, ReleaseChannel.For(channel, platform));

    [Fact]
    public void Platforms_never_share_a_channel()
    {
        // The whole reason the platform is in the name: one feed per platform, so a Linux install
        // is never offered a Windows package.
        var names = Platforms
            .Select(p => ReleaseChannel.For(UpdateChannel.Stable, p))
            .ToHashSet();

        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Streams_never_share_a_channel() =>
        Assert.NotEqual(
            ReleaseChannel.For(UpdateChannel.Stable, "linux"),
            ReleaseChannel.For(UpdateChannel.Nightly, "linux"));

    [Fact]
    public void Current_platform_is_one_of_the_three_the_build_matrix_produces() =>
        Assert.Contains(ReleaseChannel.CurrentPlatform, Platforms);
}
