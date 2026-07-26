namespace Kontena.Core.Models;

/// <summary>A login for a container registry, as the engine needs it to authenticate a pull.</summary>
/// <param name="Host">Registry host, e.g. <c>ghcr.io</c> or <c>docker.io</c>.</param>
/// <param name="Username">Account name.</param>
/// <param name="Secret">Password or access token. Never logged, never persisted outside the keychain.</param>
public sealed record RegistryCredential(string Host, string Username, string Secret);

/// <summary>
/// Where a credential came from. Shown in Settings, because "why does this registry work when I never
/// logged in here?" deserves an answer — and because Kontena did not store the ones it inherited.
/// </summary>
public enum RegistryCredentialSource
{
    /// <summary>Entered in Kontena; the secret is in the OS keychain.</summary>
    Kontena = 0,

    /// <summary>Found in the engine's own config (<c>~/.docker/config.json</c> or containers auth).</summary>
    EngineConfig,
}

/// <summary>
/// A registry Kontena knows a login for, without the secret. This is what gets persisted in settings —
/// the secret itself lives in the keychain, keyed by host.
/// </summary>
/// <param name="Host">Registry host.</param>
/// <param name="Username">Account name, so the list can say who you are on each registry.</param>
/// <param name="Source">Kontena's own, or inherited from the engine's config.</param>
public sealed record RegistryLogin(string Host, string Username, RegistryCredentialSource Source);

/// <summary>
/// Turns an image reference into the registry host that would serve it — the piece a credential has to
/// be matched on.
/// <para>
/// Nothing about this is guessable from the string alone, which is why it is here with tests rather than
/// inline at the call site: <c>nginx</c> and <c>library/nginx</c> are Docker Hub, <c>localhost:5000/app</c>
/// is a local registry, and <c>my.registry/app</c> is only distinguishable from <c>user/app</c> by
/// whether the first segment looks like a hostname at all.
/// </para>
/// </summary>
public static class RegistryHost
{
    /// <summary>What an unqualified reference means. Docker Hub's canonical name.</summary>
    public const string DockerHub = "docker.io";

    /// <summary>
    /// The host serving <paramref name="reference"/>. Returns <see cref="DockerHub"/> when the reference
    /// carries no registry of its own, which is the common case.
    /// </summary>
    public static string For(string? reference)
    {
        var text = (reference ?? string.Empty).Trim();
        if (text.Length == 0)
            return DockerHub;

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return DockerHub;                                // "nginx", "nginx:1.27"

        var first = text[..slash];

        // The rule Docker itself uses: the first segment is a registry only if it looks like one — it has
        // a dot, or a port, or is exactly "localhost". Without that, "user/app" would be read as the
        // registry "user", and every Docker Hub pull under an account would look like a private registry.
        var looksLikeHost =
            first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal)
            || first.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        return looksLikeHost ? first.ToLowerInvariant() : DockerHub;
    }

    /// <summary>
    /// Whether two hosts mean the same registry. Docker Hub is the awkward one: its credentials are
    /// filed under several spellings, and an entry under any of them is a login for all of them.
    /// </summary>
    public static bool SameHost(string a, string b) => Canonical(a) == Canonical(b);

    /// <summary>
    /// One spelling per registry. Docker Hub appears in the wild as <c>docker.io</c>,
    /// <c>index.docker.io</c>, <c>registry-1.docker.io</c> and the full legacy v1 URL — the last of which
    /// is what <c>docker login</c> still writes into <c>config.json</c>.
    /// </summary>
    public static string Canonical(string? host)
    {
        var text = (host ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
            return DockerHub;

        // Strip a scheme and any path, so "https://index.docker.io/v1/" reduces to the host.
        var withoutScheme = text.Contains("://", StringComparison.Ordinal)
            ? text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : text;
        var hostOnly = withoutScheme.Split('/', 2)[0];

        return hostOnly is "docker.io" or "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com"
            ? DockerHub
            : hostOnly;
    }
}
