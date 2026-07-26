using System.Runtime.InteropServices;

namespace Kontena.Core.Models;

/// <summary>
/// Names the release stream a build belongs to, as one string that the packaging step and the
/// running app must agree on: <c>linux-stable</c>, <c>win-nightly</c>, and so on.
/// <para>
/// The platform is part of the name on purpose. A channel is one feed, and a feed that mixed all
/// three operating systems would offer a Windows package to a Linux install — the updater has no
/// way to tell them apart once they share a channel. Splitting per platform is the packaging
/// convention; keeping the mapping here means the workflow and the client cannot drift apart,
/// because both derive the name from this one rule.
/// </para>
/// </summary>
public static class ReleaseChannel
{
    /// <summary>The channel id for a stream on a given platform, e.g. <c>osx-stable</c>.</summary>
    /// <param name="channel">Which stream.</param>
    /// <param name="platform">Platform moniker: <c>win</c>, <c>linux</c> or <c>osx</c>.</param>
    public static string For(UpdateChannel channel, string platform) =>
        $"{platform}-{Stream(channel)}";

    /// <summary>
    /// The stream part of a channel id. These strings are also what <c>build.yml</c> resolves as its
    /// channel, which is why they are spelled out rather than derived from the enum name — the
    /// workflow's vocabulary is the contract, not C#'s casing.
    /// </summary>
    public static string Stream(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Nightly => "nightly",
        UpdateChannel.Preview => "preview",
        _ => "stable",
    };

    /// <summary>The channel id for a stream on the platform this process is running on.</summary>
    public static string ForCurrentPlatform(UpdateChannel channel) => For(channel, CurrentPlatform);

    /// <summary>
    /// The platform moniker for this process, matching what the build workflow passes to the
    /// packaging tool. Anything that is not Windows or macOS is treated as Linux, which is what the
    /// build matrix produces.
    /// </summary>
    public static string CurrentPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
        : "linux";

    /// <summary>
    /// Which stream a build belongs to, read from its own version string (KON-123).
    /// <para>
    /// The workflow publishes a nightly as <c>0.2.0-nightly.20260726.26</c> and a preview as
    /// <c>0.2.0-preview.…</c>, and passes that same string to both the compiler and the packaging step.
    /// So the prerelease tag is not a hint about the channel — it is the string the channel was named
    /// from, which is why nothing else needs to be shipped alongside the binary to answer this.
    /// </para>
    /// <para>
    /// Anything without one of those tags is <see cref="UpdateChannel.Stable"/>: a tagged release has no
    /// prerelease part, and a development build has no update feed to be wrong about.
    /// </para>
    /// </summary>
    /// <param name="version">
    /// An informational version, e.g. <c>0.2.0-nightly.20260726.26+abc1234</c>. Build metadata after
    /// <c>+</c> is ignored — SourceLink appends the commit there, and it is not part of the version.
    /// </param>
    public static UpdateChannel FromVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return UpdateChannel.Stable;

        var withoutMetadata = version.Split('+', 2)[0];

        var dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
            return UpdateChannel.Stable;

        // The tag is the first dot-separated identifier of the prerelease part: "nightly.20260726.26"
        // starts with "nightly". Matching the whole prerelease would break the moment the workflow
        // changes what it appends after it.
        var prerelease = withoutMetadata[(dash + 1)..];
        var tag = prerelease.Split('.', 2)[0];

        return tag.Equals(Stream(UpdateChannel.Nightly), StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Nightly
            : tag.Equals(Stream(UpdateChannel.Preview), StringComparison.OrdinalIgnoreCase)
                ? UpdateChannel.Preview
                : UpdateChannel.Stable;
    }
}
