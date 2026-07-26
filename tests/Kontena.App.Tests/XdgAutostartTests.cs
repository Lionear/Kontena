using Kontena.App.Services;

namespace Kontena.App.Tests;

/// <summary>
/// Autostart is a change outside the app, in a file the user's desktop also writes to — so what
/// matters is not that we wrote something, but that what we report matches what is actually there.
/// Every test points the config root at a temp directory; none of them touch a real profile.
/// </summary>
public sealed class XdgAutostartTests : IDisposable
{
    private readonly string _configHome =
        Path.Combine(Path.GetTempPath(), "kontena-autostart-" + Guid.NewGuid().ToString("N"));

    private string EntryPath => Path.Combine(_configHome, "autostart", "kontena.desktop");

    private XdgAutostart Subject(string target = "/opt/Kontena.AppImage") => new(target, _configHome);

    public void Dispose()
    {
        try { Directory.Delete(_configHome, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Starts_off_when_nothing_is_registered() =>
        Assert.False(Subject().IsEnabled());

    [Fact]
    public void Enabling_writes_an_entry_and_reports_the_result_of_reading_it_back()
    {
        var autostart = Subject();

        Assert.True(autostart.Apply(true));
        Assert.True(File.Exists(EntryPath));
        Assert.True(autostart.IsEnabled());
    }

    [Fact]
    public void Disabling_removes_the_entry()
    {
        var autostart = Subject();
        autostart.Apply(true);

        Assert.False(autostart.Apply(false));
        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void Disabling_when_nothing_is_registered_is_not_an_error() =>
        Assert.False(Subject().Apply(false));

    [Fact]
    public void The_entry_launches_the_target_and_quotes_it()
    {
        // A path with spaces is ordinary — an AppImage in "~/My Apps" would otherwise become two
        // arguments and silently fail at login, which is the whole failure mode this feature has.
        var autostart = Subject("/opt/My Apps/Kontena-linux-stable.AppImage");
        autostart.Apply(true);

        var entry = File.ReadAllText(EntryPath);
        Assert.Contains("Exec=\"/opt/My Apps/Kontena-linux-stable.AppImage\"", entry, StringComparison.Ordinal);
        Assert.Contains("Type=Application", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_the_desktop_switched_off_reads_as_off()
    {
        // GNOME and KDE do not delete the file when you disable a startup item; they add Hidden=true.
        // Reporting that as "on" is how our switch and the system end up disagreeing.
        var autostart = Subject();
        autostart.Apply(true);
        File.AppendAllText(EntryPath, "Hidden=true\n");

        Assert.False(autostart.IsEnabled());
    }

    [Fact]
    public void Hidden_false_still_reads_as_on()
    {
        var autostart = Subject();
        autostart.Apply(true);
        File.AppendAllText(EntryPath, "Hidden=false\n");

        Assert.True(autostart.IsEnabled());
    }

    [Fact]
    public void Re_enabling_after_the_user_removed_the_file_by_hand_works()
    {
        var autostart = Subject();
        autostart.Apply(true);
        File.Delete(EntryPath);

        Assert.False(autostart.IsEnabled());
        Assert.True(autostart.Apply(true));
    }
}
