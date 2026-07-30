using Kontena.Core.Orchestration;
namespace Kontena.App.Services;

/// <summary>
/// Where credentials live: the operating system's own keychain, never a file of ours (KON-52).
/// <para>
/// <c>CONTRIBUTING.md</c> states this as a rule — engine credentials and secrets are stored in the OS
/// keychain and never written to disk in plaintext, logged, or transmitted anywhere other than the
/// engine being connected to. This is the interface that makes it true.
/// </para>
/// <para>
/// There is deliberately no fallback. If the platform has no keychain, or it cannot be reached, that is
/// reported through <see cref="IsAvailable"/> and the feature that wanted to store something is not
/// offered. Writing the secret somewhere else instead would break the only promise this type exists to
/// keep.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Whether a keychain is actually usable here. False on a platform without an implementation, and
    /// false when the service cannot be reached — a headless session with no Secret Service running is
    /// an ordinary situation, not an error.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stores <paramref name="secret"/> under <paramref name="key"/>, replacing anything already there.
    /// Returns false when the keychain refused; callers must treat that as "not stored" rather than
    /// assuming it worked.
    /// </summary>
    ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default);

    /// <summary>The secret stored under <paramref name="key"/>, or null when there is none.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Removes the secret under <paramref name="key"/>. Removing something that is not there is not an
    /// error — the caller wanted it gone, and it is gone.
    /// </summary>
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// How a secret is named in the keychain. Its own type because the user sees these strings: they show
/// up in Seahorse, KWallet and Keychain Access, and "kontena: registry ghcr.io" is a row someone can
/// understand and revoke, where an opaque hash is not.
/// </summary>
public static class SecretKeys
{
    /// <summary>Prefix on every key, so Kontena's entries are recognisable and removable as a group.</summary>
    public const string Prefix = "kontena";

    /// <summary>The login for a container registry, keyed by its host.</summary>
    public static string Registry(string host) => $"{Prefix}:registry:{Normalize(host)}";

    /// <summary>Credentials for a remote engine, keyed by the endpoint it was reached on.</summary>
    public static string Engine(string endpoint) => $"{Prefix}:engine:{Normalize(endpoint)}";

    /// <summary>
    /// A label for the keychain UI. Deliberately readable rather than the key itself: the point of the
    /// entry appearing in the user's own keychain manager is that they can tell what it is.
    /// </summary>
    /// <remarks>
    /// Split into at most three parts: a host or endpoint contains colons of its own —
    /// <c>localhost:5000</c> for a registry, <c>ssh://build-01</c> for an engine — and splitting on all
    /// of them drops the label back to the raw key, which is the one thing it exists not to be.
    /// </remarks>
    public static string Describe(string key) => key.Split(':', 3) switch
    {
        [_, "registry", var host] => $"Kontena — registry login for {host}",
        [_, "engine", var endpoint] => $"Kontena — engine credentials for {endpoint}",
        _ => $"Kontena — {key}",
    };

    /// <summary>
    /// Lower-cased and stripped of whitespace, so <c>GHCR.io</c> and <c>ghcr.io </c> are one entry
    /// rather than two that shadow each other.
    /// </summary>
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

/// <summary>A store for platforms and sessions without a keychain. Says no, and stores nothing.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public ValueTask<bool> SetAsync(string key, string secret, CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask DeleteAsync(string key, CancellationToken ct = default) => ValueTask.CompletedTask;
}

/// <summary>Picks the keychain for this platform.</summary>
public static class SecretStore
{
    /// <summary>
    /// The store for the current platform, or one that stores nothing where there is none.
    /// <para>
    /// Each platform's store answers <see cref="ISecretStore.IsAvailable"/> by actually calling its
    /// backend, and one that cannot is replaced here by the store that refuses. So an unreachable
    /// keychain becomes "the feature is not offered" rather than a failure on first save — and never a
    /// quiet fallback to a file.
    /// </para>
    /// </summary>
    public static ISecretStore Create()
    {
        ISecretStore? store = null;
        if (OperatingSystem.IsLinux())
            store = new LibSecretStore();
        else if (OperatingSystem.IsWindows())
            store = new WindowsCredentialStore();
        else if (OperatingSystem.IsMacOS())
            store = new MacKeychainStore();

        return store is { IsAvailable: true } ? store : new UnavailableSecretStore();
    }
}
