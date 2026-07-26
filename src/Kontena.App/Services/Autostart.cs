using System.IO;

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
        if (!OperatingSystem.IsLinux())
            return new UnsupportedAutostart();

        var target = AppLaunchTarget.Resolve();
        return target is null ? new UnsupportedAutostart() : new XdgAutostart(target);
    }
}
