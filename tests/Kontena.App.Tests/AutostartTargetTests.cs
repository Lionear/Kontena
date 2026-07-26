using Kontena.App.Services;

namespace Kontena.App.Tests;

/// <summary>
/// The per-platform *rules* — what path to point at, and what to write — rather than the writes
/// themselves. Deliberately separated in the code so they can be asserted on any machine: the file
/// write is a few lines that either work or throw, while getting the path or the quoting wrong fails
/// silently at login, once per login, for as long as nobody notices.
/// </summary>
public class AutostartTargetTests
{
    // ── macOS: launch the bundle, not the binary inside it ───────────────────

    [Theory]
    [InlineData("/Applications/Kontena.app/Contents/MacOS", "/Applications/Kontena.app")]
    [InlineData("/Applications/Kontena.app", "/Applications/Kontena.app")]
    [InlineData("/Users/rick/Applications/Kontena.app/Contents/MacOS/", "/Users/rick/Applications/Kontena.app")]
    public void Finds_the_app_bundle_above_the_executable(string contentDir, string expected) =>
        Assert.Equal(expected, AppLaunchTarget.BundleFor(contentDir));

    [Fact]
    public void Reports_no_bundle_when_there_is_none() =>
        // An install that is not a bundle has no valid macOS target, and "no target" must stay a no
        // rather than becoming a path to the bare executable.
        Assert.Null(AppLaunchTarget.BundleFor("/opt/kontena/bin"));

    // ── Windows: the value has to survive a space in the path ───────────────

    [Fact]
    public void Quotes_the_run_value()
    {
        // C:\Program Files\… is the normal case, and unquoted it reads as a command plus arguments.
        var value = RunEntry.Value(@"C:\Program Files\Kontena\current\Kontena.App.exe");

        Assert.Equal(@"""C:\Program Files\Kontena\current\Kontena.App.exe""", value);
    }

    // ── macOS: the plist launchd will read ──────────────────────────────────

    [Fact]
    public void The_plist_launches_the_bundle_through_open()
    {
        // Running the binary inside the bundle gives a process without its bundle identity: no icon,
        // and no login item the user can manage in System Settings.
        var plist = LaunchAgentAutostart.Plist("/Applications/Kontena.app");

        Assert.Contains("<string>/usr/bin/open</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>-a</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/Applications/Kontena.app</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plist_is_labelled_and_runs_at_load()
    {
        var plist = LaunchAgentAutostart.Plist("/Applications/Kontena.app");

        Assert.Contains($"<string>{LaunchAgentAutostart.Label}</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>", plist, StringComparison.Ordinal);
        Assert.StartsWith("<?xml", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void The_launch_agent_writes_reads_and_removes_its_plist()
    {
        // The file half, which is the same code on every OS — run against a temp home so it is
        // meaningful here and not only on a Mac.
        var home = Path.Combine(Path.GetTempPath(), "kontena-agent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var autostart = new LaunchAgentAutostart("/Applications/Kontena.app", home);

            Assert.False(autostart.IsEnabled());
            Assert.True(autostart.Apply(true));
            Assert.True(File.Exists(Path.Combine(home, "Library", "LaunchAgents", LaunchAgentAutostart.Label + ".plist")));
            Assert.False(autostart.Apply(false));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
