using Avalonia;
using Avalonia.Headless;
using Kontena.Sdk;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The application carries Kontena's name (KON-356).
/// <para>
/// On macOS this is the string next to the Apple logo, and the one in "About …", "Hide …" and
/// "Quit …": Avalonia builds that menu itself and takes the name from <see cref="Application.Name"/>,
/// not from the bundle's <c>CFBundleName</c>. Unset, the property defaults to "Avalonia Application" —
/// which shipped, because a correct <c>Info.plist</c> looks like it should have covered this and no
/// other platform shows the property at all.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ApplicationNameTests(HeadlessSessionFixture headless)
{
    private HeadlessUnitTestSession Session => headless.Session;

    [Fact]
    public Task Application_is_named_after_the_product() =>
        Session.Dispatch(() => Assert.Equal(ProductInfo.Name, Application.Current?.Name), default);
}
