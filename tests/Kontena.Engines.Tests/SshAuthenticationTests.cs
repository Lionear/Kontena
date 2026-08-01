using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.Engines.Tests;

/// <summary>
/// How the ssh command line says which credential to use (KON-261, KON-259).
/// <para>
/// Both settings exist because "let ssh decide" is not always reachable: an <c>IdentityAgent</c> line
/// pinned to a password manager makes a key outside it invisible, and a host that only takes passwords
/// cannot be reached by a key at all. Neither may weaken the default, which is why the assertions
/// below are as much about what is <i>absent</i> as about what is there.
/// </para>
/// </summary>
public class SshAuthenticationTests
{
    private static RemoteEngine Remote(string? keyFile = null, bool usePassword = false) =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 2222, "deploy",
            KeyFile: keyFile, UsePassword: usePassword);

    private static IReadOnlyList<string> Arguments(RemoteEngine remote) =>
        SshTunnel.Arguments(remote, "/run/user/1000/kontena-r1.sock");

    private static string Line(RemoteEngine remote) => string.Join(' ', Arguments(remote));

    // ── A named key file (KON-261) ────────────────────────────────────────────

    [Fact]
    public void A_named_key_is_passed_as_an_identity()
    {
        var arguments = Arguments(Remote(keyFile: "/home/rick/.ssh/id_ed25519"));

        Assert.Contains("-i", arguments, StringComparer.Ordinal);
        Assert.Contains("/home/rick/.ssh/id_ed25519", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void A_named_key_is_the_only_one_offered()
    {
        // Without this ssh offers everything the agent holds first. A host with a low MaxAuthTries
        // then refuses the connection before the chosen key is ever tried — and the error says
        // "Permission denied (publickey)", which points at the key that never got a turn.
        Assert.Contains("IdentitiesOnly=yes", Line(Remote(keyFile: "/home/rick/.ssh/id_ed25519")), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_key_file_ssh_still_decides()
    {
        // The default has to stay untouched: an agent and an ssh_config that already work are the
        // reason SSH is the easy transport, and naming a file is the exception.
        var line = Line(Remote());

        Assert.DoesNotContain("-i ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("IdentitiesOnly", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_file_that_is_really_an_ssh_option_never_reaches_the_command_line() =>
        // Same gate as the host, the user and the socket path (KON-181).
        Assert.Throws<ArgumentException>(() => Arguments(Remote(keyFile: "-oProxyCommand=id")));

    // ── A password (KON-259) ──────────────────────────────────────────────────

    [Fact]
    public void Batch_mode_stays_on_unless_a_password_was_chosen()
    {
        // BatchMode is what turns a prompt into an error instead of a hang. It comes off for exactly
        // one configuration, and it must not come off for any other.
        Assert.Contains("BatchMode=yes", Line(Remote()), StringComparison.Ordinal);
        Assert.Contains("BatchMode=yes", Line(Remote(keyFile: "/home/rick/.ssh/id_ed25519")), StringComparison.Ordinal);
        Assert.Contains("BatchMode=no", Line(Remote(usePassword: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_connection_does_not_spend_its_attempts_on_keys() =>
        Assert.Contains(
            "PreferredAuthentications=password,keyboard-interactive",
            Line(Remote(usePassword: true)),
            StringComparison.Ordinal);

    [Fact]
    public void The_engine_itself_has_nowhere_to_hold_a_secret()
    {
        // RemoteEngine is serialised into settings.json, and its own summary promises "nothing secret
        // is in here". A password field added later would keep that promise only until someone filled
        // it in — so the guard is on the shape of the type, not on a caller remembering.
        //
        // It is also what keeps the password out of argv: SshTunnel.Arguments is built from this
        // record and nothing else, which is the difference between SSH_ASKPASS and sshpass -p.
        var suspicious = typeof(RemoteEngine).GetProperties()
            .Select(p => p.Name)
            .Where(name =>
                name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Token", StringComparison.OrdinalIgnoreCase))

            // The flag is a choice, not a credential: it says which method to use and holds nothing.
            .Where(name => name != nameof(RemoteEngine.UsePassword))
            .ToList();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void The_askpass_helper_names_a_keychain_entry_and_not_a_secret()
    {
        // What travels is the name of the entry. The password itself is read by the helper, from the
        // keychain, inside its own process.
        var helper = new SshAskpass("/opt/kontena/Kontena", "kontena:engine:r1");

        Assert.Equal("kontena:engine:r1", helper.SecretKey);
        Assert.Equal("KONTENA_ASKPASS_SECRET", SshAskpass.SecretVariable);
    }
}
