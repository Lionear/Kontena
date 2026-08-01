using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;

namespace Kontena.Engines.Tests;

/// <summary>
/// Telling ssh's two host key failures apart, and trusting one of them (KON-260).
/// <para>
/// Kontena connects with <c>BatchMode=yes</c>, so ssh may not ask anything and a host nobody has
/// connected to yet simply fails. Reading that failure correctly is what decides whether the user is
/// offered a fingerprint or a warning — and those two must never be swapped.
/// </para>
/// </summary>
public class SshHostKeyTests
{
    private static RemoteEngine Remote() =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 2222, "deploy");

    // ── Reading ssh's complaint ───────────────────────────────────────────────

    [Fact]
    public void A_host_nobody_has_trusted_yet_is_unknown() =>
        Assert.Equal(
            SshHostKeyProblem.Unknown,
            SshHostKeys.Classify("Host key verification failed."));

    [Fact]
    public void A_changed_key_is_not_read_as_an_unknown_one()
    {
        // ssh prints its banner *and* ends with "Host key verification failed", so a check that looks
        // for the second line first would offer to trust a key that changed underneath the user —
        // which is the one case where trusting is exactly wrong.
        const string complaint = """
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!
            Add correct host key in /home/user/.ssh/known_hosts to get rid of this message.
            Offending ECDSA key in /home/user/.ssh/known_hosts:42
            Host key verification failed.
            """;

        Assert.Equal(SshHostKeyProblem.Changed, SshHostKeys.Classify(complaint));
    }

    [Fact]
    public void Strict_checking_on_a_host_with_no_entry_is_the_same_problem() =>
        // Same situation, different wording: ssh says this instead when StrictHostKeyChecking=yes.
        Assert.Equal(
            SshHostKeyProblem.Unknown,
            SshHostKeys.Classify(
                "No ED25519 host key is known for build-01 and you have requested strict checking."));

    [Fact]
    public void An_authentication_failure_is_not_a_host_key_failure() =>
        // The host was trusted fine; the key the user offered was not accepted. Different fix, and
        // dressing it up as a fingerprint question would send them to the wrong place entirely.
        Assert.Equal(
            SshHostKeyProblem.None,
            SshHostKeys.Classify("deploy@build-01: Permission denied (publickey)."));

    [Fact]
    public void An_unreachable_host_is_not_a_host_key_failure() =>
        Assert.Equal(SshHostKeyProblem.None, SshHostKeys.Classify("ssh: connect to host build-01 port 22: No route to host"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Silence_is_not_a_host_key_failure(string? complaint) =>
        Assert.Equal(SshHostKeyProblem.None, SshHostKeys.Classify(complaint));

    // ── What the user is told ─────────────────────────────────────────────────

    [Fact]
    public void An_unknown_host_is_worded_as_a_question_the_user_can_answer()
    {
        var failure = SshHostKeys.Failure(SshHostKeyProblem.Unknown, Remote(), "Host key verification failed.");

        Assert.Contains("build-01", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fingerprint", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_changed_key_never_reads_as_something_to_click_through()
    {
        // The difference between the two messages is the whole point: one invites a decision, the
        // other refuses to make one.
        var failure = SshHostKeys.Failure(SshHostKeyProblem.Changed, Remote(), "…");

        Assert.DoesNotContain("Review its fingerprint", failure.Message, StringComparison.Ordinal);
        Assert.Contains("intercepted", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sshs_own_words_are_kept_whole()
    {
        // It names the file and the line number of the offending entry, which is what someone needs
        // to undo it. Summarising that away would leave them looking for it by hand.
        const string complaint = "Offending ECDSA key in /home/user/.ssh/known_hosts:42";
        var failure = SshHostKeys.Failure(SshHostKeyProblem.Changed, Remote(), complaint);

        Assert.Equal(complaint, failure.Complaint);
    }

    // ── Writing known_hosts ───────────────────────────────────────────────────

    [Fact]
    public async Task Trusting_a_host_appends_the_scanned_lines()
    {
        using var file = new TemporaryFile();
        await SshHostKeys.TrustAsync([Key("ssh-ed25519 AAAAC3"), Key("ssh-rsa AAAAB3")], file.Path);

        var lines = await File.ReadAllLinesAsync(file.Path);

        Assert.Equal(["ssh-ed25519 AAAAC3", "ssh-rsa AAAAB3"], lines);
    }

    [Fact]
    public async Task A_file_without_a_trailing_newline_does_not_gain_a_glued_line()
    {
        // An unparseable known_hosts line is ignored without a word, so gluing two entries together
        // would leave the host untrusted with nothing at all to show for it.
        using var file = new TemporaryFile();
        await File.WriteAllTextAsync(file.Path, "existing.example ssh-ed25519 AAAAOLD");

        await SshHostKeys.TrustAsync([Key("build-01 ssh-ed25519 AAAANEW")], file.Path);
        var lines = await File.ReadAllLinesAsync(file.Path);

        Assert.Equal(
            ["existing.example ssh-ed25519 AAAAOLD", "build-01 ssh-ed25519 AAAANEW"],
            lines);
    }

    [Fact]
    public async Task Existing_entries_are_left_alone()
    {
        using var file = new TemporaryFile();
        await File.WriteAllTextAsync(file.Path, "existing.example ssh-ed25519 AAAAOLD\n");

        await SshHostKeys.TrustAsync([Key("build-01 ssh-ed25519 AAAANEW")], file.Path);

        Assert.Contains("existing.example ssh-ed25519 AAAAOLD", await File.ReadAllTextAsync(file.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scanning_a_host_that_cannot_be_reached_says_what_ssh_said()
    {
        // Port 1 on loopback answers nothing. The point is the shape of the failure: one line, in
        // ssh's words. The first version shelled out to ssh-keyscan and surfaced its banner five times
        // over — once per key type it tried — which is what the user actually saw.
        var unreachable = new RemoteEngine(
            "r2", "Nowhere", RemoteEngineTransport.Ssh, "127.0.0.1", 1, "deploy");

        var failure = await Assert.ThrowsAsync<EngineException>(() => SshHostKeys.ScanAsync(unreachable));

        Assert.DoesNotContain('\n', failure.Message);
    }

    [Fact]
    public async Task Trusting_nothing_writes_nothing()
    {
        // A scan that came back empty must not create or touch the file: "no keys" is a failure to
        // report, not an empty edit to make.
        using var file = new TemporaryFile(create: false);
        await SshHostKeys.TrustAsync([], file.Path);

        Assert.False(File.Exists(file.Path));
    }

    private static SshHostKey Key(string line) => new("ssh-ed25519", "SHA256:test", line);

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"kontena-known-hosts-{Guid.NewGuid():N}");

            if (create)
                File.WriteAllText(Path, string.Empty);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover in the temp directory is not worth failing a test over.
            }
        }
    }
}
