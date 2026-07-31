namespace Kontena.Sdk.Models;

/// <summary>How Kontena reaches an engine that is not on this machine.</summary>
public enum RemoteEngineTransport
{
    /// <summary>
    /// Over SSH, by forwarding the remote engine's socket to a local one. What <c>DOCKER_HOST=ssh://…</c>
    /// does, and what most people already have working: existing keys, existing agent, nothing to
    /// generate or distribute.
    /// </summary>
    Ssh = 0,

    /// <summary>
    /// Straight to a TCP port. Requires TLS client certificates unless explicitly allowed without —
    /// an unauthenticated Docker socket on a network port hands root on that host to anyone who can
    /// reach it.
    /// </summary>
    Tcp,
}

/// <summary>
/// A remote engine as the user configured it. Persisted in settings; nothing secret is in here — an SSH
/// key passphrase or a certificate password belongs in the keychain, keyed by <see cref="Id"/>.
/// </summary>
/// <param name="Id">Stable id, so the keychain entry and the remembered choices survive a rename.</param>
/// <param name="Name">What the switcher shows.</param>
/// <param name="Transport">SSH or TCP.</param>
/// <param name="Host">Hostname or address. For SSH this may be an <c>ssh_config</c> alias.</param>
/// <param name="Port">SSH port, or the engine's TCP port. Null means the transport's default.</param>
/// <param name="User">SSH user. Null lets ssh decide, which respects <c>ssh_config</c>.</param>
/// <param name="SocketPath">Remote socket to forward. Null means the engine's usual path.</param>
/// <param name="CertificateDirectory">
/// Directory holding <c>ca.pem</c>, <c>cert.pem</c> and <c>key.pem</c> — the same layout
/// <c>DOCKER_CERT_PATH</c> uses, so an existing setup can be pointed at rather than rebuilt.
/// </param>
/// <param name="AllowInsecureTcp">
/// Explicit acknowledgement that this TCP endpoint has no TLS. False by default and never set on the
/// user's behalf: it is the difference between a private connection and an open door.
/// </param>
/// <param name="KeyFile">
/// A private key to authenticate with, or null to let ssh decide (KON-261). Null is still the right
/// default — an agent and an <c>ssh_config</c> that already work should keep working — but it is not
/// always reachable: an <c>IdentityAgent</c> line pinned to 1Password or Secretive means ssh ignores
/// <c>SSH_AUTH_SOCK</c> entirely, and a key outside that agent is invisible with no way to point at it.
/// </param>
/// <param name="UsePassword">
/// Whether to authenticate with a password from the keychain instead of a key (KON-259). Off by
/// default, and only ever on because the user chose it for this engine: it is what turns
/// <c>BatchMode</c> off, and that guard is the difference between an error and a hang.
/// </param>
public sealed record RemoteEngine(
    string Id,
    string Name,
    RemoteEngineTransport Transport,
    string Host,
    int? Port = null,
    string? User = null,
    string? SocketPath = null,
    string? CertificateDirectory = null,
    bool AllowInsecureTcp = false,
    string? KeyFile = null,
    bool UsePassword = false)
{
    /// <summary>The backend id this appears under, unique per configured remote.</summary>
    public string Backend => $"docker-remote:{Id}";

    /// <summary>The remote socket to forward over SSH when none was given.</summary>
    public const string DefaultSocketPath = "/var/run/docker.sock";

    /// <summary>Docker's TLS port. 2375 is the unencrypted one and is not a default here on purpose.</summary>
    public const int DefaultTlsPort = 2376;

    /// <summary>What the user is connecting to, in one line, for the switcher's second row.</summary>
    public string Endpoint => Transport switch
    {
        RemoteEngineTransport.Ssh => $"ssh://{(User is { Length: > 0 } u ? u + "@" : string.Empty)}{Host}"
            + (Port is { } p ? $":{p}" : string.Empty),
        _ => $"tcp://{Host}:{Port ?? DefaultTlsPort}",
    };

    /// <summary>
    /// Why these values cannot be handed to <c>ssh</c>, or null (KON-181).
    /// <para>
    /// A process argument list stops a shell from interpreting anything, but it does not stop
    /// <c>ssh</c> from reading an argument as one of its own <b>options</b>. A host of
    /// <c>-oProxyCommand=…</c> is a command <c>ssh</c> runs, under this user's account, and there is no
    /// <c>--</c> terminator for its destination to hide behind. A socket path containing <c>:</c> is
    /// the same shape of problem one level down: it rewrites the <c>-L</c> forward spec, so the tunnel
    /// carries traffic somewhere other than where it says.
    /// </para>
    /// <para>
    /// One rule, two callers: this gate and <c>SshTunnel.Arguments</c>. Today the user types these
    /// values themselves, so it is self-inflicted; it stops being self-inflicted the moment a remote
    /// arrives from somewhere else — a synced settings file, an imported Docker context, a connection
    /// string someone was asked to paste.
    /// </para>
    /// </summary>
    public static string? ArgumentProblem(
        string? host, string? user, string? socketPath, string? keyFile = null)
    {
        if (host is { Length: > 0 } h && h.StartsWith('-'))
            return "A host cannot start with \"-\". SSH would read it as one of its own options rather than a destination.";

        if (user is { Length: > 0 } u && u.StartsWith('-'))
            return "A user cannot start with \"-\". SSH would read it as one of its own options rather than a name.";

        if (socketPath is { Length: > 0 } s && s.Contains(':', StringComparison.Ordinal))
            return "A socket path cannot contain \":\". It would change which address the tunnel forwards to.";

        // Same rule, third field (KON-261). A colon is harmless here — this one is not part of a
        // forward spec — but a leading hyphen is read as an option exactly as it is everywhere else.
        if (keyFile is { Length: > 0 } k && k.StartsWith('-'))
            return "A key file cannot start with \"-\". SSH would read it as one of its own options rather than a path.";

        return null;
    }

    /// <summary>
    /// Why this configuration cannot be used, or null when it can. Checked before a connection is
    /// attempted so the complaint names the field rather than surfacing as a transport error later.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
                return "A host is required.";

            if (ArgumentProblem(Host, User, SocketPath, KeyFile) is { } unsafeValue)
                return unsafeValue;

            if (Transport == RemoteEngineTransport.Ssh)
            {
                // Caught here rather than at connect time, where ssh reports a missing key as
                // "Permission denied (publickey)" — a message that points at the host while the
                // problem is on this machine's own disk.
                if (KeyFile is { Length: > 0 } key && !File.Exists(key))
                    return $"No key file at {key}.";

                // Both at once is not a configuration with a meaning: ssh would try the key and then
                // ask for a password anyway, so the setting that was chosen last would silently win.
                if (UsePassword && KeyFile is { Length: > 0 })
                    return "Choose either a key file or a password, not both.";
            }

            if (Transport == RemoteEngineTransport.Tcp)
            {
                var hasCerts = !string.IsNullOrWhiteSpace(CertificateDirectory);
                if (!hasCerts && !AllowInsecureTcp)
                {
                    return "A TCP engine needs TLS certificates. Without them the connection is "
                        + "unauthenticated and unencrypted, which gives anyone who can reach the port "
                        + "control of that host.";
                }
            }

            return null;
        }
    }
}

/// <summary>
/// A remote engine as a form still holds it — raw text, nothing validated yet (KON-118).
/// <para>
/// Two places now describe the same connection: the add wizard and the Settings page. Building the
/// <see cref="RemoteEngine"/> from one type keeps the awkward parts — which fields belong to which
/// transport, what an empty box means — in one place. Fields that do not apply to the chosen transport
/// are dropped rather than carried along, so a path typed into the SSH form cannot come back as a
/// certificate directory after switching to TCP.
/// </para>
/// </summary>
public sealed record RemoteEngineDraft
{
    /// <summary>What the switcher shows. Falls back to the host when left empty.</summary>
    public string Name { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;
    public string User { get; init; } = string.Empty;

    /// <summary>Free text: an unparseable or non-positive port means "the transport's default".</summary>
    public string Port { get; init; } = string.Empty;

    public string SocketPath { get; init; } = string.Empty;
    public string CertificateDirectory { get; init; } = string.Empty;
    public bool AllowInsecure { get; init; }

    /// <summary>Private key to authenticate with. Empty means "let ssh decide", which is the default.</summary>
    public string KeyFile { get; init; } = string.Empty;

    /// <summary>Whether to use a password from the keychain rather than a key (KON-259).</summary>
    public bool UsePassword { get; init; }

    /// <summary>SSH is the default: it is the transport most people already have working.</summary>
    public bool IsSsh { get; init; } = true;

    /// <summary>Certificates are only meaningful over TCP, and only when the user gave a directory.</summary>
    public bool HasCertificates => !IsSsh && !string.IsNullOrWhiteSpace(CertificateDirectory);

    /// <summary>
    /// Whether the TCP endpoint would be unauthenticated. Shown while the form is being filled in,
    /// which is the moment the choice is actually being made.
    /// </summary>
    public bool IsInsecureTcp => !IsSsh && string.IsNullOrWhiteSpace(CertificateDirectory);

    /// <summary>The engine this form describes. <paramref name="id"/> is generated when not given.</summary>
    public RemoteEngine Build(string? id = null)
    {
        var port = int.TryParse(Port.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0 ? parsed : (int?)null;

        var host = Host.Trim();
        var user = User.Trim();
        var socket = SocketPath.Trim();
        var certificates = CertificateDirectory.Trim();
        var key = KeyFile.Trim();

        return new RemoteEngine(
            id ?? Guid.NewGuid().ToString("N")[..12],
            string.IsNullOrWhiteSpace(Name) ? host : Name.Trim(),
            IsSsh ? RemoteEngineTransport.Ssh : RemoteEngineTransport.Tcp,
            host,
            port,
            IsSsh && user.Length > 0 ? user : null,
            IsSsh && socket.Length > 0 ? socket : null,
            !IsSsh && certificates.Length > 0 ? certificates : null,
            !IsSsh && AllowInsecure,

            // Dropped along with the rest of the SSH fields when the form is on TCP, for the same
            // reason: a path typed into one transport must not come back under the other.
            IsSsh && key.Length > 0 ? key : null,
            IsSsh && UsePassword);
    }

    /// <summary>Why this form cannot be used yet, or null. Delegates to the model's own rule.</summary>
    public string? Problem => Build("draft").Problem;
}
