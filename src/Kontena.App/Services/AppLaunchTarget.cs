using System.IO;
using Velopack.Locators;

namespace Kontena.App.Services;

/// <summary>
/// The path that starts this copy of Kontena again, for anything outside the app that needs to launch
/// it — today only autostart (KON-103).
/// <para>
/// This is the part that goes wrong quietly, so it is deliberately its own step. An autostart entry
/// pointing at the wrong path fails at login with no message, and you find out a week later.
/// <see cref="System.Environment.ProcessPath"/> is not it: under <c>dotnet run</c> it names the SDK
/// host, and inside an AppImage it names a temporary mount that a later version will not have.
/// </para>
/// </summary>
internal static class AppLaunchTarget
{
    /// <summary>
    /// A path that will still start Kontena after the next update, or null when no such path can be
    /// determined — a development run, or an unpacked archive someone may move. Callers must treat
    /// null as "do not offer this", never as "guess".
    /// </summary>
    public static string? Resolve()
    {
        // An AppImage tells you where it is: the runtime sets APPIMAGE to the absolute path of the
        // file itself, while ProcessPath points inside the throwaway mount. The packaging step names
        // the file per channel and not per version (Kontena-linux-stable.AppImage), so this path
        // survives an update in place.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
            return appImage;

        // A Velopack install keeps the current version behind a stable directory, so what lives there
        // is what to point at rather than today's versioned folder.
        try
        {
            if (VelopackLocator.IsCurrentSet)
            {
                var content = VelopackLocator.Current.AppContentDir;
                if (!string.IsNullOrEmpty(content))
                    return FromContentDir(content);
            }
        }
        catch (Exception)
        {
            // No Velopack context (a test host, a plain build) — which is a "no", handled below.
        }

        return null;
    }

    /// <summary>
    /// The launch path inside an installed app's content directory, or null when it does not hold what
    /// this platform needs. Separate and internal so the per-platform rule is testable anywhere: it is
    /// the rule, not the file write, that decides whether autostart works.
    /// </summary>
    internal static string? FromContentDir(string contentDir)
    {
        // macOS launches bundles, not executables. Opening the binary inside a .app directly gives a
        // process without its bundle identity — no icon, no login-item entry the user can manage — so
        // walk up to the bundle and hand that over instead.
        if (OperatingSystem.IsMacOS())
        {
            var bundle = BundleFor(contentDir);
            return bundle is not null && Directory.Exists(bundle) ? bundle : null;
        }

        var exe = Path.Combine(contentDir, ExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The nearest <c>.app</c> directory at or above <paramref name="path"/>, or null.</summary>
    /// <remarks>
    /// Walks on <c>/</c> rather than through <see cref="Path"/>. A bundle path is a macOS path
    /// whatever machine is looking at it, and the <see cref="Path"/> members follow the *host* — so
    /// on Windows this rewrote <c>/Applications/Kontena.app</c> as <c>\Applications\Kontena.app</c>
    /// and the tests asserting the rule failed on the one platform that cannot check it for real.
    /// Testable anywhere was the point of splitting this out; that only holds if it parses the same
    /// way anywhere.
    /// </remarks>
    internal static string? BundleFor(string path)
    {
        var current = path.TrimEnd('/');
        while (!string.IsNullOrEmpty(current))
        {
            if (current.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current;

            var slash = current.LastIndexOf('/');
            if (slash <= 0)
                break;

            current = current[..slash];
        }

        return null;
    }

    private static string ExecutableName =>
        OperatingSystem.IsWindows() ? "Kontena.App.exe" : "Kontena.App";
}
