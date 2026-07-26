using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Kontena.App.Services;

/// <summary>
/// Starting Kontena when the user logs in. A system-level change outside the app, so it happens only
/// on an explicit action and never as a side effect of anything else (KON-103).
/// </summary>
public interface IAutostart
{
    /// <summary>
    /// Whether this can be offered at all. False on a platform without an implementation, and false
    /// for an install whose path cannot be pinned down — see <see cref="AppLaunchTarget"/>. The
    /// Settings row is hidden when this is false, because a switch that cannot work must not be shown.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether Kontena is actually registered to start at login, read from the system rather than
    /// from our settings file. The two can disagree — the entry can be removed by hand or switched
    /// off in the desktop's own settings — and the system is the one telling the truth.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// Register or unregister, then confirm by reading back. Returns what the system says afterwards,
    /// not what was asked for: a switch that stays on while nothing was written is the same lie in a
    /// different shape.
    /// </summary>
    bool Apply(bool enabled);
}

/// <summary>An autostart that cannot do anything, for platforms and installs where that is the truth.</summary>
public sealed class UnsupportedAutostart : IAutostart
{
    public bool IsSupported => false;

    public bool IsEnabled() => false;

    public bool Apply(bool enabled) => false;
}

/// <summary>
/// XDG autostart: a <c>.desktop</c> entry in <c>~/.config/autostart</c>, which every mainstream Linux
/// desktop reads. No elevation, no daemon, and the user can undo it in their own settings UI —
/// which is exactly why <see cref="IsEnabled"/> reads the file instead of trusting our own record.
/// </summary>
public sealed class XdgAutostart : IAutostart
{
    private readonly string _target;
    private readonly string _path;

    public XdgAutostart(string target, string? configHome = null)
    {
        _target = target;
        var root = configHome
            ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _path = Path.Combine(root, "autostart", "kontena.desktop");
    }

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        try
        {
            if (!File.Exists(_path))
                return false;

            // The desktop spec's own way of saying "registered but off", written by GNOME and KDE when
            // you disable an entry in their settings. Present-but-disabled is not enabled.
            foreach (var line in File.ReadAllLines(_path))
            {
                var text = line.Trim();
                if (text.StartsWith("Hidden=", StringComparison.OrdinalIgnoreCase))
                    return !text["Hidden=".Length..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }
        catch (Exception)
        {
            // Unreadable is not "on": say no rather than show a switch we cannot stand behind.
            return false;
        }
    }

    public bool Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, Entry(_target));
            }
            else if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
            // Fall through to the read-back, which is what the caller acts on either way.
        }

        return IsEnabled();
    }

    /// <summary>
    /// <c>Exec</c> is quoted because the path can contain spaces, and an AppImage often lives in a
    /// directory that has them.
    /// </summary>
    private static string Entry(string target) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Kontena
        Comment=Manage containers through one UI, whichever engine is underneath
        Exec="{target}"
        Icon=kontena
        Terminal=false
        Categories=Development;System;
        X-GNOME-Autostart-enabled=true

        """;
}


/// <summary>
/// Windows: a value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Per-user and
/// writable without elevation — this must never ask for admin, so HKLM is not an option.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutostart : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kontena";

    private readonly string _target;

    public RegistryAutostart(string target) => _target = target;

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Task Manager's Startup tab does not delete the value when you switch an entry off; it
            // records the decision separately. Present-but-disapproved is not enabled, and reporting it
            // as on is how our switch and the system end up disagreeing.
            return !IsDisapproved();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether Task Manager (or Settings › Startup apps) has switched this entry off. The state lives
    /// in <c>StartupApproved\Run</c> as a binary blob whose first byte carries the flag; anything with
    /// bit 0 set is enabled, and the values Windows writes for "off" have it clear.
    /// </summary>
    private static bool IsDisapproved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 0;
        }
        catch (Exception)
        {
            // Absent is the normal case for an entry nobody has touched: not disapproved.
            return false;
        }
    }

    public bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return false;

            if (enabled)
                key.SetValue(ValueName, RunEntry.Value(_target));
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Fall through to the read-back, which is what the caller acts on either way.
        }

        return IsEnabled();
    }

}

/// <summary>
/// The value written under the Run key. Plain string formatting, deliberately outside the
/// Windows-gated class: it is the part that can be wrong on any machine, so it must be callable — and
/// assertable — on any machine.
/// </summary>
internal static class RunEntry
{
    /// <summary>
    /// Quoted, because <c>C:\Program Files\…</c> is the normal case and an unquoted path with a space
    /// is read as a command plus arguments — which fails at login, silently, exactly once per login.
    /// </summary>
    internal static string Value(string target) => $"\"{target}\"";
}

/// <summary>
/// macOS: a LaunchAgent plist in <c>~/Library/LaunchAgents</c>. Plain file, no API binding, and it
/// shows up in System Settings › General › Login Items where the user can turn it off — which is why
/// <see cref="IsEnabled"/> reads the file rather than trusting our own record.
/// </summary>
public sealed class LaunchAgentAutostart : IAutostart
{
    /// <summary>Reverse-DNS, as launchd expects, and matching the app's own identity.</summary>
    internal const string Label = "app.kontena.Kontena";

    private readonly string _target;
    private readonly string _path;

    public LaunchAgentAutostart(string target, string? home = null)
    {
        _target = target;
        var root = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _path = Path.Combine(root, "Library", "LaunchAgents", Label + ".plist");
    }

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(_path)
                && File.ReadAllText(_path).Contains(Label, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, Plist(_target));
            }
            else if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
            // Fall through to the read-back.
        }

        return IsEnabled();
    }

    /// <summary>
    /// <c>open -a</c> rather than a bare path: the target is a <c>.app</c> bundle, and launching a
    /// bundle is what gives the process its identity — its icon, and an entry the user can manage.
    /// Executing the binary inside it would start Kontena as an anonymous process instead.
    /// </summary>
    internal static string Plist(string bundlePath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>/usr/bin/open</string>
                <string>-a</string>
                <string>{bundlePath}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>

        """;
}

/// <summary>Picks the autostart mechanism for this platform and install.</summary>
public static class Autostart
{
    /// <summary>
    /// The mechanism for the current platform, or an unsupported one when there is none.
    /// <para>
    /// Only Linux is implemented so far. Windows (the <c>HKCU…\Run</c> key) and macOS (a LaunchAgent,
    /// or <c>SMAppService</c> on recent versions) are separate tickets rather than untested writes to
    /// someone's login items: the failure mode of getting this wrong is silent, so shipping it blind
    /// is worse than not shipping it. On those platforms the row stays hidden, which is the state the
    /// ticket asks for — the feature works, or it is not there.
    /// </para>
    /// </summary>
    public static IAutostart Create()
    {
        // No path means no entry worth writing — the same rule on every platform. That is what keeps
        // the failure mode "the switch is not offered" instead of "login silently does nothing".
        var target = AppLaunchTarget.Resolve();
        if (target is null)
            return new UnsupportedAutostart();

        if (OperatingSystem.IsLinux())
            return new XdgAutostart(target);

        if (OperatingSystem.IsWindows())
            return new RegistryAutostart(target);

        if (OperatingSystem.IsMacOS())
            return new LaunchAgentAutostart(target);

        return new UnsupportedAutostart();
    }
}
