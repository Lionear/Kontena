using System.Runtime.InteropServices;

namespace Kontena.Sdk.Shell;

/// <summary>
/// Puts a freshly opened pseudo-terminal into the output mode an interactive terminal expects
/// (KON-171).
/// <para>
/// The PTY comes up with output post-processing switched off — <c>stty -a</c> inside it reports
/// <c>-opost -onlcr</c> — so a program printing <c>\n</c> emits a bare line feed. A line feed moves the
/// cursor down and nothing else, so every line starts where the previous one ended and the output walks
/// diagonally across the screen. It is not an alignment problem; it is a missing carriage return.
/// </para>
/// <para>
/// This is the fallback, not the main repair. A shell copies the terminal's settings while it starts
/// and puts that copy back before running each command, so a change made from outside is one it undoes
/// — which is why every shell Kontena recognises runs <c>stty opost onlcr</c> in the startup file it is
/// given. What is left is the shell Kontena does not recognise, where there is no startup file to write
/// into and this is the only thing there is.
/// </para>
/// <para>
/// Fixed at the terminal rather than in the renderer either way. The alternative — putting the emulator
/// into line-feed/new-line mode so it supplies the carriage return itself — hides the symptom while
/// leaving the tty in a state no program running on it expects.
/// </para>
/// <para>
/// Windows has no termios and its ConPTY does not behave this way, so this does nothing there.
/// </para>
/// </summary>
internal static class PosixTerminalModes
{
    private const int TCSANOW = 0;

    /// <summary>Enable output post-processing at all. Same bit on Linux and macOS.</summary>
    private const ulong OPOST = 0x1;

    /// <summary>Map a line feed to carriage-return + line feed. Differs per platform.</summary>
    private const ulong ONLCR_LINUX = 0x4;
    private const ulong ONLCR_MACOS = 0x2;

    /// <summary>
    /// <c>struct termios</c> is bigger than this on no platform we run on (60 bytes on Linux, 72 on
    /// macOS); the C library writes only as many bytes as its own definition has.
    /// </summary>
    private const int TermiosSize = 128;

    /// <summary>
    /// Turn on <c>OPOST</c> and <c>ONLCR</c> for the terminal behind <paramref name="handle"/>.
    /// </summary>
    /// <remarks>
    /// Only <c>c_oflag</c> is touched, and it is read back out of the same buffer the C library filled,
    /// so the rest of the structure is passed through untouched and its layout never has to be modelled.
    /// Its <em>offset</em> does: <c>c_oflag</c> is the second field, after <c>c_iflag</c>, which is 32
    /// bits on Linux and 64 on macOS.
    /// </remarks>
    /// <returns>True when the mode was set; false when it could not be, which is not fatal.</returns>
    public static bool EnableOutputPostProcessing(SafeHandle handle)
    {
        if (OperatingSystem.IsWindows())
            return false;

        var fd = (int)handle.DangerousGetHandle();
        if (fd < 0)
            return false;

        var buffer = Marshal.AllocHGlobal(TermiosSize);

        try
        {
            for (var i = 0; i < TermiosSize; i++)
                Marshal.WriteByte(buffer, i, 0);

            if (tcgetattr(fd, buffer) != 0)
                return false;

            var wide = OperatingSystem.IsMacOS();
            var offset = wide ? 8 : 4;
            var bits = OPOST | (wide ? ONLCR_MACOS : ONLCR_LINUX);

            if (wide)
            {
                var flags = (ulong)Marshal.ReadInt64(buffer, offset);
                Marshal.WriteInt64(buffer, offset, (long)(flags | bits));
            }
            else
            {
                var flags = (uint)Marshal.ReadInt32(buffer, offset);
                Marshal.WriteInt32(buffer, offset, (int)(flags | (uint)bits));
            }

            return tcsetattr(fd, TCSANOW, buffer) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, IntPtr termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, IntPtr termios);
}
