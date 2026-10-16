using System.IO;
using Avalonia.Platform;

namespace Kontena.App.Services;

/// <summary>
/// Puts a Linux AppImage in the user's application menu on first start (KON-444): a <c>.desktop</c>
/// entry in <c>~/.local/share/applications</c> and the icon under the XDG icon theme, the way other
/// AppImages do it themselves. An AppImage is one file you download and run, so nothing else would.
/// <para>
/// Only for an AppImage: a package or a <c>dotnet run</c> has its own way into the menu, or none.
/// </para>
/// </summary>
public sealed class DesktopMenuEntry(string target, string dataHome)
{
    private readonly string _entry = Path.Combine(dataHome, "applications", "kontena.desktop");
    private readonly string _icon = Path.Combine(dataHome, "icons", "hicolor", "512x512", "apps", "kontena.png");

    /// <summary>
    /// The entry for the running AppImage, if this is one. Best-effort: a menu entry that could not be
    /// written is not worth a word at startup.
    /// </summary>
    public static void EnsureForThisAppImage()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Set by the AppImage runtime to the file itself. Velopack updates that file in place, so the
        // entry keeps working across updates.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage) || !File.Exists(appImage))
            return;

        try
        {
            new DesktopMenuEntry(appImage, DataHome())
                .Ensure(BundledIcon);
        }
        catch (Exception)
        {
            // Read-only home, odd permissions: the app runs the same without a menu entry.
        }
    }

    /// <summary>
    /// Writes the entry and the icon unless an entry already launches <c>target</c>. Returns whether it
    /// wrote. An entry that points here is left alone, edits included; one that points at a moved or
    /// renamed AppImage is rewritten, or the menu keeps launching a file that is gone.
    /// </summary>
    internal bool Ensure(Func<Stream> icon)
    {
        if (File.Exists(_entry) && File.ReadAllLines(_entry).Contains(ExecLine(target)))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(_icon)!);
        using (var source = icon())
        using (var file = File.Create(_icon))
            source.CopyTo(file);

        Directory.CreateDirectory(Path.GetDirectoryName(_entry)!);
        File.WriteAllText(_entry, Entry(target));
        return true;
    }

    /// <summary>
    /// The desktop entry that launches <paramref name="target"/>, shared with the autostart entry so
    /// the two cannot describe Kontena differently. <c>Exec</c> is quoted because an AppImage often
    /// lives in a directory with spaces in its name.
    /// </summary>
    internal static string Entry(string target) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Kontena
        Comment=Manage containers through one UI, whichever engine is underneath
        {ExecLine(target)}
        Icon=kontena
        Terminal=false
        Categories=Development;System;

        """;

    /// <summary>The app's own 512px icon, the one the AppImage itself carries.</summary>
    internal static Stream BundledIcon() => AssetLoader.Open(new Uri("avares://Kontena.App/Assets/icon.png"));

    private static string ExecLine(string target) => $"Exec=\"{target}\"";

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
}
