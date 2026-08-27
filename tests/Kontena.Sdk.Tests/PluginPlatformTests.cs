using System.Globalization;

namespace Kontena.Sdk.Tests;

/// <summary>
/// The rule a plugin's platform declaration is read by (KON-280). Written against the machine the test
/// runs on rather than a mocked OS: the whole value of the field is that it answers about <em>this</em>
/// machine, and a fake host would only prove the arithmetic.
/// </summary>
public sealed class PluginPlatformTests
{
    /// <summary>The current host, named the way a manifest would name it.</summary>
    private static string ThisOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";

    /// <summary>An operating system this is not, so "does not match" has something to be about.</summary>
    private static string OtherOs => OperatingSystem.IsWindows() ? "linux" : "windows";

    private static Version HostVersion => Environment.OSVersion.Version;

    [Fact]
    public void No_declaration_means_every_platform()
    {
        Assert.True(PluginPlatform.SupportsHost([]));
    }

    [Fact]
    public void A_plugin_for_this_os_matches()
    {
        Assert.True(PluginPlatform.SupportsHost([new PluginPlatform { Os = ThisOs }]));
    }

    [Fact]
    public void A_plugin_for_another_os_does_not()
    {
        Assert.False(PluginPlatform.SupportsHost([new PluginPlatform { Os = OtherOs }]));
    }

    [Fact]
    public void One_matching_entry_is_enough()
    {
        Assert.True(PluginPlatform.SupportsHost(
            [new PluginPlatform { Os = OtherOs }, new PluginPlatform { Os = ThisOs }]));
    }

    [Fact]
    public void The_os_name_is_matched_case_insensitively()
    {
        Assert.True(new PluginPlatform { Os = ThisOs.ToUpperInvariant() }.MatchesHost());
    }

    [Fact]
    public void A_version_floor_below_this_machine_matches()
    {
        Assert.True(new PluginPlatform { Os = ThisOs, MinVersion = "1.0.0" }.MatchesHost());
    }

    [Fact]
    public void A_version_floor_above_this_machine_does_not()
    {
        var above = HostVersion.Major + 1;

        Assert.False(new PluginPlatform { Os = ThisOs, MinVersion = $"{above}.0.0" }.MatchesHost());
    }

    /// <summary>
    /// "macOS 26" is how the requirement is written down — Apple's <c>container</c> is the case this
    /// field was added for — and <c>Version.TryParse</c> refuses a single number on its own.
    /// </summary>
    [Fact]
    public void A_major_only_floor_is_a_version()
    {
        var here = HostVersion.Major.ToString(CultureInfo.InvariantCulture);
        var above = (HostVersion.Major + 1).ToString(CultureInfo.InvariantCulture);

        Assert.True(new PluginPlatform { Os = ThisOs, MinVersion = here }.MatchesHost());
        Assert.False(new PluginPlatform { Os = ThisOs, MinVersion = above }.MatchesHost());
    }

    /// <summary>
    /// The safe direction: a floor nobody can read must not become no floor at all, or a plugin that
    /// meant macOS 26 and mistyped it runs on macOS 13.
    /// </summary>
    [Fact]
    public void An_unreadable_floor_is_a_no_rather_than_no_floor()
    {
        Assert.False(new PluginPlatform { Os = ThisOs, MinVersion = "26.o" }.MatchesHost());
        Assert.False(new PluginPlatform { Os = ThisOs, MinVersion = "1.0.0-beta" }.MatchesHost());
    }

    [Fact]
    public void An_os_this_build_has_never_heard_of_does_not_match()
    {
        Assert.False(new PluginPlatform { Os = "haiku" }.MatchesHost());
    }

    /// <summary>What a rejection and a store listing read as — the record's generated ToString does not.</summary>
    [Fact]
    public void It_reads_as_the_requirement_it_is()
    {
        Assert.Equal("linux", new PluginPlatform { Os = "linux" }.ToString());
        Assert.Equal("macos 26", new PluginPlatform { Os = "macos", MinVersion = "26" }.ToString());
    }
}
