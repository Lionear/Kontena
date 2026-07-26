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
    [InlineData(UpdateChannel.Preview, "win", "win-preview")]
    [InlineData(UpdateChannel.Preview, "linux", "linux-preview")]
    [InlineData(UpdateChannel.Preview, "osx", "osx-preview")]
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
    public void Streams_never_share_a_channel()
    {
        // Every stream the Build workflow publishes is selectable in the app, so every one of them
        // needs its own feed on every platform: three streams times three platforms, no collisions.
        var all = Enum.GetValues<UpdateChannel>()
            .SelectMany(c => Platforms.Select(p => ReleaseChannel.For(c, p)))
            .ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(9, all.Count);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, "stable")]
    [InlineData(UpdateChannel.Preview, "preview")]
    [InlineData(UpdateChannel.Nightly, "nightly")]
    public void Stream_names_match_the_workflow_vocabulary(UpdateChannel channel, string expected) =>
        // These are the literal values build.yml resolves as CHANNEL. If one drifts, the app asks for
        // a feed nobody publishes.
        Assert.Equal(expected, ReleaseChannel.Stream(channel));

    [Fact]
    public void Current_platform_is_one_of_the_three_the_build_matrix_produces() =>
        Assert.Contains(ReleaseChannel.CurrentPlatform, Platforms);

    // ── Reading a build's own stream (KON-123) ───────────────────────────────

    [Theory]
    [InlineData("0.2.0-nightly.20260726.26", UpdateChannel.Nightly)]
    [InlineData("0.2.0-preview.20260726.3", UpdateChannel.Preview)]
    [InlineData("0.2.0", UpdateChannel.Stable)]
    [InlineData("1.0.0", UpdateChannel.Stable)]
    public void The_prerelease_tag_names_the_stream(string version, UpdateChannel expected) =>
        // These are the strings build.yml actually stamps, which is why the tag can be trusted: it is
        // the same word the channel was named from, not a hint about it.
        Assert.Equal(expected, ReleaseChannel.FromVersion(version));

    [Fact]
    public void Build_metadata_is_not_part_of_the_version()
    {
        // SourceLink appends "+<commit>". Reading it as part of the version would misread every build
        // the moment that is switched on.
        Assert.Equal(UpdateChannel.Nightly, ReleaseChannel.FromVersion("0.2.0-nightly.20260726.26+9f8e7d6"));
        Assert.Equal(UpdateChannel.Stable, ReleaseChannel.FromVersion("0.2.0+9f8e7d6"));
    }

    [Fact]
    public void Only_the_first_prerelease_identifier_is_read()
    {
        // The workflow appends a date and a run number after the tag. Matching the whole prerelease
        // would break the next time that shape changes.
        Assert.Equal(UpdateChannel.Nightly, ReleaseChannel.FromVersion("0.2.0-nightly"));
        Assert.Equal(UpdateChannel.Nightly, ReleaseChannel.FromVersion("0.2.0-nightly.1.2.3.4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_unreadable_version_is_treated_as_stable(string? version) =>
        // A host that stamps nothing has no update feed to be wrong about, and stable offers the least.
        Assert.Equal(UpdateChannel.Stable, ReleaseChannel.FromVersion(version));

    [Fact]
    public void An_unknown_prerelease_tag_is_not_guessed_at()
    {
        // "rc" is not a channel Kontena publishes. Treating it as one would point the updater at a feed
        // that does not exist.
        Assert.Equal(UpdateChannel.Stable, ReleaseChannel.FromVersion("0.2.0-rc.1"));
        Assert.Equal(UpdateChannel.Stable, ReleaseChannel.FromVersion("0.2.0-alpha"));
    }

    [Fact]
    public void The_tag_is_read_regardless_of_casing() =>
        Assert.Equal(UpdateChannel.Nightly, ReleaseChannel.FromVersion("0.2.0-Nightly.1"));

    [Fact]
    public void What_it_reads_is_what_the_packaging_step_writes()
    {
        // The round trip that matters: the stream name in a channel id and the tag in a version are the
        // same word, so a build cannot end up reporting a channel it was not published on.
        foreach (var channel in Enum.GetValues<UpdateChannel>())
        {
            var stream = ReleaseChannel.Stream(channel);
            var version = channel == UpdateChannel.Stable ? "0.2.0" : $"0.2.0-{stream}.20260726.1";

            Assert.Equal(channel, ReleaseChannel.FromVersion(version));
        }
    }
}
