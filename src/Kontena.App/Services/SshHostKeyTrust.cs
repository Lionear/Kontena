using Kontena.App.ViewModels;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Services;

/// <summary>
/// The question Kontena asks the first time it meets an SSH host (KON-260).
/// <para>
/// Two places connect to a remote engine — the add wizard and the Settings page — and both used to end
/// at "connect once by hand and accept the key", which is a terminal instruction inside a desktop app.
/// The question itself is the same in both, so it is built here rather than twice.
/// </para>
/// </summary>
internal static class SshHostKeyTrust
{
    /// <summary>
    /// Scans the host and builds the confirmation. Scanning is trust-on-first-use — the fingerprint is
    /// only as good as the network it arrived over — which is exactly why the answer is put in front of
    /// a person instead of being acted on here.
    /// </summary>
    /// <param name="remote">The engine being connected to.</param>
    /// <param name="afterTrusting">Run once the user has said yes, normally the connection attempt again.</param>
    public static async Task<ConfirmRequest> AskAsync(
        RemoteEngine remote, Func<Task> afterTrusting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(afterTrusting);

        var keys = await SshHostKeys.ScanAsync(remote, ct).ConfigureAwait(false);
        return Build(remote, keys, async () =>
        {
            await SshHostKeys.TrustAsync(keys, ct: ct).ConfigureAwait(false);
            await afterTrusting().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The question itself, given keys that have already been fetched. Separate from the scan so that
    /// what it says can be tested without a host to say it about.
    /// </summary>
    internal static ConfirmRequest Build(
        RemoteEngine remote, IReadOnlyList<SshHostKey> keys, Func<Task> onConfirm)
    {
        // Type alongside fingerprint: a host offers several keys and ssh picks one, so the line the
        // user compares has to say which algorithm it belongs to or they will compare the wrong pair.
        var details = keys
            .Select(key => new ConfirmDetail("IconHash", key.Fingerprint, key.KeyType))
            .ToList();

        return new ConfirmRequest(
            $"Trust {remote.Host}?",
            $"Kontena has not connected to {remote.Host} before. Anyone can offer a key; only the real "
            + "host has the one below. Check it against the host itself before accepting — on that "
            + "machine, ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub prints the same line.",
            "Trust and connect",
            onConfirm,

            // Nothing is destroyed here, and dressing it in the delete-red used for "this goes away"
            // would spend a warning the user needs elsewhere (KON-126).
            Destructive: false,
            details);
    }
}
