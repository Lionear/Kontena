using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Engines.Tests;

/// <summary>
/// How the ssh process is launched, as opposed to what it is told to do.
/// <para>
/// None of this shows up on the platform it is developed on. A missing <c>CreateNoWindow</c> is
/// invisible on Linux and macOS and a black console window on Windows, which is how it shipped.
/// </para>
/// </summary>
public class SshProcessStartTests
{
    private static RemoteEngine Remote(bool usePassword = false) =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 2222, "deploy",
            UsePassword: usePassword);

    [Fact]
    public void No_console_window_is_created()
    {
        // Kontena is a GUI application with no console of its own, so Windows makes one for any console
        // program it starts — and keeps it on screen for as long as the tunnel lives.
        Assert.True(SshTunnel.StartInfo(Remote(), "/tmp/s.sock").CreateNoWindow);
    }

    [Fact]
    public void The_shell_is_not_involved()
    {
        // Both matter together: UseShellExecute would ignore the argument list this whole class is
        // careful about, and it cannot redirect the streams the failure messages are read from.
        var start = SshTunnel.StartInfo(Remote(), "/tmp/s.sock");

        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardError);
        Assert.True(start.RedirectStandardOutput);
    }

    [Fact]
    public void The_arguments_are_passed_as_a_list_not_a_string()
    {
        // A single string would put quoting rules between Kontena and ssh, which is where the argument
        // gate of KON-181 would stop being the last word.
        var start = SshTunnel.StartInfo(Remote(), "/tmp/s.sock");

        Assert.Equal(string.Empty, start.Arguments);
        Assert.Contains("-N", start.ArgumentList, StringComparer.Ordinal);
    }

    [Fact]
    public void A_password_engine_is_told_where_to_get_one()
    {
        var start = SshTunnel.StartInfo(
            Remote(usePassword: true), "/tmp/s.sock", new SshAskpass("/opt/kontena/Kontena", "kontena:engine:r1"));

        Assert.Equal("/opt/kontena/Kontena", start.Environment["SSH_ASKPASS"], StringComparer.Ordinal);
        Assert.Equal("kontena:engine:r1", start.Environment[SshAskpass.SecretVariable], StringComparer.Ordinal);

        // Without force, ssh consults the helper only when DISPLAY is set — a rule about X11 that has
        // nothing to do with whether this process can type.
        Assert.Equal("force", start.Environment["SSH_ASKPASS_REQUIRE"], StringComparer.Ordinal);
    }

    [Fact]
    public void An_engine_that_uses_a_key_is_never_pointed_at_the_helper()
    {
        // Not "the variable is absent": ProcessStartInfo starts from this process's own environment,
        // and a desktop session commonly sets SSH_ASKPASS already — KDE exports
        // /usr/bin/ksshaskpass. What matters is that Kontena does not aim ssh at its own helper, and
        // does not name a keychain entry, for a connection nobody asked to use a password.
        //
        // The inherited one is inert: measured, BatchMode=yes does not consult SSH_ASKPASS at all,
        // even with SSH_ASKPASS_REQUIRE=force. That is also what makes BatchMode the guard it is.
        var start = SshTunnel.StartInfo(
            Remote(), "/tmp/s.sock", new SshAskpass("/opt/kontena/Kontena", "kontena:engine:r1"));

        Assert.NotEqual(
            "/opt/kontena/Kontena",
            start.Environment.TryGetValue("SSH_ASKPASS", out var helper) ? helper : null,
            StringComparer.Ordinal);

        Assert.False(start.Environment.ContainsKey(SshAskpass.SecretVariable));
    }
}
