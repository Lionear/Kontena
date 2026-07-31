using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Sdk;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// What the two connection screens do with an SSH host key failure (KON-260).
/// <para>
/// Reading ssh's complaint is tested next to the command line, in <c>SshHostKeyTests</c>. These are
/// about the consequence: which failure puts a fingerprint in front of the user, and — the one that
/// matters — which one must never do that.
/// </para>
/// </summary>
public sealed class SshHostKeyTrustTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-hostkey-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static RemoteEngine Remote() =>
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 2222, "deploy");

    private static AddBackendViewModel Wizard() =>
        new(new SettingsStore(Path.GetTempFileName()), [], onClose: () => { }, onAdded: _ => Task.CompletedTask);

    private SettingsViewModel Form()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings();
        store.Save(settings);

        return new SettingsViewModel(
            store, settings, [],
            autostart: new UnsupportedAutostart(),
            secrets: new UnavailableSecretStore())
        {
            RemoteName = "Build server",
            RemoteHost = "build-01",
        };
    }

    // ── The wizard ────────────────────────────────────────────────────────────

    [Fact]
    public void A_host_nobody_has_trusted_yet_is_offered_for_review()
    {
        var wizard = Wizard();

        wizard.Fail(Remote(), SshHostKeys.Failure(
            SshHostKeyProblem.Unknown, Remote(), "Host key verification failed."));

        Assert.True(wizard.CanReviewHostKey);
        Assert.Equal(Remote(), wizard.UntrustedHost);
    }

    [Fact]
    public void A_changed_host_key_is_never_offered_for_review()
    {
        // The entire point of the check. An offer here would turn the one warning that means "someone
        // may be sitting between you and that host" into a button that makes it go away.
        var wizard = Wizard();

        wizard.Fail(Remote(), SshHostKeys.Failure(
            SshHostKeyProblem.Changed, Remote(), "REMOTE HOST IDENTIFICATION HAS CHANGED"));

        Assert.False(wizard.CanReviewHostKey);
        Assert.Null(wizard.UntrustedHost);
        Assert.Contains("changed", wizard.FailureHeadline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_the_host_refused_is_not_a_host_key_question()
    {
        // Right host, wrong credentials: the fix is the user's key, not the host's.
        var wizard = Wizard();

        wizard.Fail(Remote(), new InvalidOperationException("deploy@build-01: Permission denied (publickey)."));

        Assert.False(wizard.CanReviewHostKey);
        Assert.Contains("refused", wizard.FailureHeadline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_offer_does_not_outlive_the_attempt_that_made_it()
    {
        // Fix the host name, test again, fail for another reason — the button from the first failure
        // would otherwise still be there, now pointing at a host nobody asked about.
        var wizard = Wizard();
        wizard.Fail(Remote(), SshHostKeys.Failure(SshHostKeyProblem.Unknown, Remote(), "Host key verification failed."));

        wizard.Fail(Remote(), new InvalidOperationException("ssh: connect to host build-01 port 22: No route to host"));

        Assert.False(wizard.CanReviewHostKey);
    }

    [Fact]
    public void The_refusal_no_longer_sends_the_user_to_a_terminal()
    {
        // It used to say "connect once by hand and accept the key" — a terminal instruction inside a
        // desktop app, and the reason a first connection to any host had never worked.
        var wizard = Wizard();

        wizard.Fail(Remote(), SshHostKeys.Failure(SshHostKeyProblem.Unknown, Remote(), "Host key verification failed."));

        Assert.DoesNotContain(wizard.FailureHints, hint => hint.Contains("by hand", StringComparison.OrdinalIgnoreCase));
    }

    // ── The Settings form ─────────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_offered_before_anything_has_been_tested()
    {
        Assert.False(Form().CanReviewHostKey);
    }

    [Fact]
    public void Editing_the_form_withdraws_the_offer()
    {
        // The offer belongs to the host that was tested; typing a different one makes it an offer to
        // trust a machine that was never contacted.
        var form = Form();
        form.UntrustedHost = Remote();

        form.RemoteHost = "build-02";

        Assert.False(form.CanReviewHostKey);
        Assert.Null(form.UntrustedHost);
    }

    // ── The question itself ───────────────────────────────────────────────────

    [Fact]
    public void The_confirmation_names_the_host_and_shows_every_fingerprint()
    {
        // A host offers several keys and ssh picks one, so all of them are shown with the algorithm
        // they belong to — comparing the wrong pair proves nothing.
        var request = SshHostKeyTrust.Build(
            Remote(),
            [
                new SshHostKey("ssh-ed25519", "SHA256:abc", "build-01 ssh-ed25519 AAAA"),
                new SshHostKey("ssh-rsa", "SHA256:def", "build-01 ssh-rsa BBBB"),
            ],
            () => Task.CompletedTask);

        Assert.Contains("build-01", request.Title, StringComparison.Ordinal);
        Assert.Equal(["SHA256:abc", "SHA256:def"], request.Details!.Select(d => d.Headline));
        Assert.Equal(["ssh-ed25519", "ssh-rsa"], request.Details!.Select(d => d.Detail));
    }

    [Fact]
    public void Trusting_a_host_is_not_dressed_up_as_destruction()
    {
        // The red confirm means "this goes away". Spending it here would make it mean less where it
        // has to be believed (KON-126).
        var request = SshHostKeyTrust.Build(
            Remote(), [new SshHostKey("ssh-ed25519", "SHA256:abc", "line")], () => Task.CompletedTask);

        Assert.False(request.Destructive);
        Assert.Equal("Trust and connect", request.ConfirmLabel);
    }

    [Fact]
    public void The_message_tells_the_user_where_to_check_the_fingerprint()
    {
        // A fingerprint fetched over the network is only worth something when compared against the
        // host itself; without saying how, the dialog is a rubber stamp.
        var request = SshHostKeyTrust.Build(
            Remote(), [new SshHostKey("ssh-ed25519", "SHA256:abc", "line")], () => Task.CompletedTask);

        Assert.Contains("ssh-keygen -lf", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_trusted_until_the_user_says_so()
    {
        // The scan happens to build the question; writing known_hosts happens only on confirm.
        var confirmed = false;
        var request = SshHostKeyTrust.Build(
            Remote(), [new SshHostKey("ssh-ed25519", "SHA256:abc", "line")], () =>
            {
                confirmed = true;
                return Task.CompletedTask;
            });

        Assert.False(confirmed);
        await request.OnConfirm();
        Assert.True(confirmed);
    }
}
