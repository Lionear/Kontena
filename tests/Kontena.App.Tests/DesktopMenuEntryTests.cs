using Kontena.App.Services;

namespace Kontena.App.Tests;

/// <summary>
/// An AppImage puts itself in the application menu on first start (KON-444). Every test points the
/// data root at a temp directory; none of them touch a real profile.
/// </summary>
public sealed class DesktopMenuEntryTests : IDisposable
{
    private static readonly byte[] Icon = [0x89, 0x50, 0x4E, 0x47];

    private readonly string _dataHome =
        Path.Combine(Path.GetTempPath(), "kontena-menu-" + Guid.NewGuid().ToString("N"));

    private string EntryPath => Path.Combine(_dataHome, "applications", "kontena.desktop");
    private string IconPath => Path.Combine(_dataHome, "icons", "hicolor", "512x512", "apps", "kontena.png");

    private bool Ensure(string target = "/opt/My Apps/Kontena-linux-stable.AppImage") =>
        new DesktopMenuEntry(target, _dataHome).Ensure(() => new MemoryStream(Icon));

    public void Dispose()
    {
        try { Directory.Delete(_dataHome, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void The_first_start_writes_a_menu_entry_that_launches_the_appimage_and_its_icon()
    {
        Assert.True(Ensure());

        var entry = File.ReadAllText(EntryPath);
        Assert.Contains("Exec=\"/opt/My Apps/Kontena-linux-stable.AppImage\"", entry, StringComparison.Ordinal);
        Assert.Contains("Icon=kontena", entry, StringComparison.Ordinal);
        Assert.Equal(Icon, File.ReadAllBytes(IconPath));
    }

    [Fact]
    public void A_later_start_leaves_an_entry_that_already_points_here_alone()
    {
        // The user may have edited it (a name, a category); a correct entry is theirs from then on.
        Ensure();
        File.AppendAllText(EntryPath, "Keywords=docker;\n");

        Assert.False(Ensure());
        Assert.Contains("Keywords=docker;", File.ReadAllText(EntryPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_appimage_points_the_entry_at_where_it_is_now()
    {
        // Otherwise the menu keeps launching a file that is no longer there.
        Ensure("/home/me/Downloads/Kontena-linux-stable.AppImage");

        Assert.True(Ensure("/home/me/Apps/Kontena-linux-stable.AppImage"));
        Assert.Contains("Exec=\"/home/me/Apps/Kontena-linux-stable.AppImage\"", File.ReadAllText(EntryPath), StringComparison.Ordinal);
    }
}
