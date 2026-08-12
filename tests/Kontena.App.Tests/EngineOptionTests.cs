using Kontena.App.ViewModels;
using Kontena.Core.Versioning;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// What a switcher row says about the version its backend reports (KON-370). The pill sits beside
/// "Connected" because that is where a backend's state already reads, and it appears only when there
/// is something wrong — an unknown is not a warning.
/// </summary>
public class EngineOptionTests
{
    private static EngineOption Row(VersionSupport? support) => new()
    {
        Backend = "docker",
        Name = "Docker",
        Chip = new BackendChipInfo("D"),
        Support = support,
    };

    [Fact]
    public void A_release_nobody_maintains_is_called_out()
    {
        var row = Row(new VersionSupport("28", IsMaintained: false, new DateOnly(2026, 5, 13), NewerPatch: null));

        Assert.True(row.IsUnsupported);
        Assert.Equal("Release 28 has not been supported since 13 May 2026.", row.SupportSummary);
    }

    [Fact]
    public void A_release_with_no_published_end_date_still_warns()
    {
        var row = Row(new VersionSupport("27", IsMaintained: false, EolFrom: null, NewerPatch: null));

        Assert.True(row.IsUnsupported);
        Assert.Equal("Release 27 is no longer supported.", row.SupportSummary);
    }

    [Fact]
    public void A_maintained_release_is_not_a_warning()
    {
        var row = Row(new VersionSupport("29", IsMaintained: true, EolFrom: null, NewerPatch: null));

        Assert.False(row.IsUnsupported);
        Assert.Equal(string.Empty, row.SupportSummary);
    }

    [Fact]
    public void A_maintained_release_behind_on_patches_says_so_without_a_warning()
    {
        var row = Row(new VersionSupport("29", IsMaintained: true, EolFrom: null, NewerPatch: "29.7.2"));

        // The second signal the same document carries. Worth a sentence, not worth a pill.
        Assert.False(row.IsUnsupported);
        Assert.Equal("29.7.2 is available.", row.SupportSummary);
    }

    [Fact]
    public void A_backend_nothing_is_known_about_says_nothing()
    {
        var row = Row(null);

        Assert.False(row.IsUnsupported);
        Assert.Equal(string.Empty, row.SupportSummary);
    }
}
