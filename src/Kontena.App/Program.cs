using Avalonia;
using System;
using System.Runtime.InteropServices;
using Kontena.App.Services;
using Kontena.Sdk;
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
        // Before anything else, including the updater hooks: this run is not the app (KON-259). ssh
        // started it to be asked for a password, and it has to answer on stdout and go away. Building
        // Velopack or Avalonia here would put a window — or an update — behind an ssh prompt.
        if (Environment.GetEnvironmentVariable(SshAskpass.SecretVariable) is { Length: > 0 } secretKey)
        {
            Environment.Exit(AnswerAskpass(secretKey));
            return;
        }

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

    /// <summary>
    /// Prints the stored password for <paramref name="secretKey"/> and nothing else (KON-259).
    /// </summary>
    /// <remarks>
    /// ssh reads one line from this process's stdout and treats it as the password. So: no logging, no
    /// banner, no diagnostics — anything else written here would be tried as a password and fail in a
    /// way that looks like the wrong password rather than a bug.
    /// <para>
    /// A missing entry exits non-zero without printing. ssh then reports its own failure, which is the
    /// truthful one: there is no password to give.
    /// </para>
    /// </remarks>
    private static int AnswerAskpass(string secretKey)
    {
        try
        {
            var secret = SecretStore.Create().GetAsync(secretKey).AsTask().GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(secret))
                return 1;

            Console.Out.Write(secret);
            Console.Out.Write('\n');
            Console.Out.Flush();
            return 0;
        }
        catch (Exception)
        {
            // The keychain refused or is not there. Saying so on stdout would hand ssh the complaint
            // as a password; the exit code is the only channel this process has.
            return 1;
        }
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
            // Without this, ExtendClientAreaToDecorationsHint is ignored on X11 and the window
            // manager keeps drawing its own title bar — so Kontena's ends up as a second one
            // underneath it (KON-138). Windows and macOS extend the client area server-side and
            // need no equivalent.
            //
            // Avalonia marks this option experimental and reserves the right to remove it. The
            // suppression is deliberate and scoped to this one line: on Linux the alternative is
            // either two title bars or no branded one at all. If it disappears in a later Avalonia,
            // this is the line that breaks, and the fallback is to stop extending on X11.
#pragma warning disable AVALONIA_X11_CSD
            .With(new X11PlatformOptions { EnableDrawnDecorations = true })
#pragma warning restore AVALONIA_X11_CSD
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
