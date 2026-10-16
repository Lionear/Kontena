using Avalonia.Headless;
using Kontena.App.Services;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The menu entry's icon comes out of the app's own resources (KON-444). Here rather than beside the
/// entry's tests because an <c>avares://</c> path only resolves inside a running Avalonia app, and a
/// wrong one would leave every menu entry with a blank icon and nothing failing.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class DesktopMenuIconTests(HeadlessSessionFixture headless)
{
    [Fact]
    public Task The_menu_entry_installs_the_bundled_png_icon() =>
        headless.Session.Dispatch(
            () =>
            {
                var dataHome = Path.Combine(Path.GetTempPath(), "kontena-menu-icon-" + Guid.NewGuid().ToString("N"));
                try
                {
                    new DesktopMenuEntry("/opt/Kontena.AppImage", dataHome).Ensure(DesktopMenuEntry.BundledIcon);

                    var icon = File.ReadAllBytes(Path.Combine(dataHome, "icons", "hicolor", "512x512", "apps", "kontena.png"));
                    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, icon[..4]);
                }
                finally
                {
                    Directory.Delete(dataHome, recursive: true);
                }
            },
            CancellationToken.None);
}
