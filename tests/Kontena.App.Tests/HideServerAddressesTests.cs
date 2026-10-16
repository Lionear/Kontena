using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// "Hide server addresses" (KON-410): the switcher keeps where a connection points off the screen
/// until you ask for one row's address, for screen shares and demos.
/// </summary>
public sealed class HideServerAddressesTests
{
    private static EngineOption Row(string detail, bool hide) => new()
    {
        Backend = "kubernetes:prod",
        Name = "prod-eu-west",
        Chip = new BackendChipInfo("K", null),
        Detail = detail,
        HideAddress = hide,
    };

    [Fact]
    public void A_hidden_address_is_masked_and_the_version_stays()
    {
        var row = Row("v1.29.4 · https://34.120.0.1", hide: true);

        Assert.Equal("v1.29.4 · ••••••••", row.DetailText);
        Assert.True(row.CanReveal);
    }

    [Fact]
    public void Revealing_shows_that_rows_address_and_hiding_it_again_masks_it()
    {
        var row = Row("v1.29.4 · https://34.120.0.1", hide: true);

        row.ToggleRevealCommand.Execute(null);
        Assert.Equal("v1.29.4 · https://34.120.0.1", row.DetailText);

        row.ToggleRevealCommand.Execute(null);
        Assert.Equal("v1.29.4 · ••••••••", row.DetailText);
    }

    [Theory]
    [InlineData("v1.29.4 · https://34.120.0.1", false)]
    [InlineData("not connected", true)]
    public void Nothing_to_hide_offers_no_eye(string detail, bool hide)
    {
        // Setting off, or a detail without an address (a reason it is down): the line is shown as it
        // is, and an eye that reveals nothing would be a dead button.
        var row = Row(detail, hide);

        Assert.Equal(detail, row.DetailText);
        Assert.False(row.CanReveal);
    }

    [Theory]
    [InlineData("v1.29.4 · https://34.120.0.1", "v1.29.4", "https://34.120.0.1")]
    [InlineData("28.1.1", "28.1.1", "")]
    public void A_detail_splits_into_version_and_address_in_one_place(string detail, string version, string address) =>
        Assert.Equal((version, address), BackendDetail.Split(detail));
}
