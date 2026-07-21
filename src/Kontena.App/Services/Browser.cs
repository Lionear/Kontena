using System.Diagnostics;

namespace Kontena.App.Services;

/// <summary>Best-effort launcher for opening URLs in the user's default browser.</summary>
internal static class Browser
{
    /// <summary>
    /// Open <paramref name="url"/> in the default browser. Best-effort: failures are
    /// swallowed, since opening a browser is never critical to the app's flow.
    /// </summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — a missing/blocked browser must not crash the app.
        }
    }
}
