using System.IO;
using Velopack.Locators;

namespace Kontena.App.Services;

/// <summary>
/// The command that starts this copy of Kontena again, for anything outside the app that needs to
/// launch it — today only autostart (KON-103).
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

        // A Velopack install keeps the current version behind a stable directory, so the executable
        // there is the one to point at rather than today's versioned folder.
        try
        {
            if (VelopackLocator.IsCurrentSet)
            {
                var content = VelopackLocator.Current.AppContentDir;
                if (!string.IsNullOrEmpty(content))
                {
                    var exe = Path.Combine(content, ExecutableName);
                    if (File.Exists(exe))
                        return exe;
                }
            }
        }
        catch (Exception)
        {
            // No Velopack context (a test host, a plain build) — which is a "no", handled below.
        }

        return null;
    }

    private static string ExecutableName =>
        OperatingSystem.IsWindows() ? "Kontena.App.exe" : "Kontena.App";
}
