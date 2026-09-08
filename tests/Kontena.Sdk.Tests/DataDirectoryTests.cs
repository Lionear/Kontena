namespace Kontena.Sdk.Tests;

/// <summary>
/// KON-421: a build from a working copy must not write where the installed app writes. Testing a
/// change with <c>dotnet run</c> twice overwrote the developer's real settings.json, because both
/// computed the same path.
/// </summary>
public sealed class DataDirectoryTests
{
    /// <summary>Where the installed app keeps its data — the directory a debug build must stay out of.</summary>
    private static readonly string Installed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "Kontena");

    /// <summary>
    /// The assertion runs in whichever configuration the suite was built in, so both halves are
    /// covered: CI builds Release, a developer's own run builds Debug.
    /// </summary>
    [Fact]
    public void A_debug_build_writes_somewhere_the_installed_app_does_not()
    {
#if DEBUG
        Assert.NotEqual(Installed, ProductInfo.DataDirectory);
        Assert.Equal(Installed + "-Dev", ProductInfo.DataDirectory);
#else
        Assert.Equal(Installed, ProductInfo.DataDirectory);
#endif
    }
}
