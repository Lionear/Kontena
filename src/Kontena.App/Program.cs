using Avalonia;
using System;
using System.Runtime.InteropServices;
using Velopack;

namespace Kontena.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must be the first thing that runs (KON-110). On the runs that install, update or uninstall
        // Kontena, this handles the hook and exits the process — so anything above it would execute
        // during an update, in a window nobody sees. The callbacks below are part of that: they run
        // inside those hook processes, never on a normal launch. Velopack declares them Windows-only
        // — the other platforms have no install hooks to run them from — so they are registered
        // behind the same guard the analyzer wants rather than left to no-op.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack = velopack
                .OnAfterInstallFastCallback(_ => RefreshShellIcons())
                .OnAfterUpdateFastCallback(_ => RefreshShellIcons());
        }

        velopack.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private const int ShcneAssocchanged = 0x08000000;
    private const uint ShcnfIdlist = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Ask Explorer to drop its cached icons (KON-132).</summary>
    /// <remarks>
    /// Explorer keys its icon cache on the target path, and Velopack keeps the installed app behind
    /// a stable one — <c>…\Kontena\current\Kontena.exe</c> is the same path before and after an
    /// update. So a release that changes the icon can leave the old one on the Start-menu shortcut
    /// until the cache happens to be rebuilt. A fresh install never sees this; only an upgrade does.
    ///
    /// SHCNE_ASSOCCHANGED is the notification installers use for exactly this, and it is all this
    /// needs to be: no window, no admin rights, and it returns immediately — the fast callbacks run
    /// in a hook process that Velopack kills if it lingers, and may not show UI.
    ///
    /// It does not reach a shortcut pinned to the taskbar, which caches its own icon under
    /// <c>User Pinned\TaskBar</c> and ignores this notification. Unpinning and re-pinning is the
    /// only reliable fix there, and that is the user's call to make, not an updater's.
    /// </remarks>
    private static void RefreshShellIcons()
    {
        // Registration is already behind a Windows guard, but this runs from a lambda the analyzer
        // cannot tie back to it — and a guard on a call this cheap costs nothing either way.
        if (!OperatingSystem.IsWindows()) return;

        SHChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
