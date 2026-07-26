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
    /// <param name="channel">Stable or nightly.</param>
    /// <param name="platform">Platform moniker: <c>win</c>, <c>linux</c> or <c>osx</c>.</param>
    public static string For(UpdateChannel channel, string platform) =>
        $"{platform}-{(channel == UpdateChannel.Nightly ? "nightly" : "stable")}";

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
}
