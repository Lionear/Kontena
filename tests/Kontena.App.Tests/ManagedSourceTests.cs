using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

public class ManagedSourceTests
{
    [Fact]
    public void NamesTheAppsWeKnow()
    {
        Assert.Equal("DataTray", Format.ManagedSource("datatray"));
    }

    [Fact]
    public void StillNamesDataTrayUnderItsFormerLabel()
    {
        // A container keeps the label it was created with for as long as it lives, so containers started
        // by SQL Explorer stay on the old value even after the user updates to DataTray. Dropping this
        // mapping would rename them to "Sqlexplorer" in the list.
        Assert.Equal("SQL Explorer", Format.ManagedSource("sqlexplorer"));
    }

    [Fact]
    public void FallsBackToTheRawValueForAnythingElse()
    {
        Assert.Equal("Portainer", Format.ManagedSource("portainer"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SaysAnotherToolWhenTheLabelIsMissing(string? source)
    {
        Assert.Equal("another tool", Format.ManagedSource(source));
    }
}
