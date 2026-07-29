using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>
/// Registry logins that already exist in the engine's own config — <c>~/.docker/config.json</c>, or the
/// containers <c>auth.json</c> Podman uses (KON-114).
/// <para>
/// <b>Read-only, always.</b> Kontena never writes to these files. They belong to another tool, a write
/// could clobber a <c>credsStore</c> arrangement, and putting a base64 password in a file would
/// contradict the promise that Kontena keeps secrets in the keychain. Its own logins go there; these are
/// inherited, and the UI says so.
/// </para>
/// <para>
/// The reason to read them at all: someone who has already run <c>docker login</c> would otherwise get
/// "pull access denied" inside Kontena while the same pull works in their terminal — an app that looks
/// broken for a reason that is invisible from the inside.
/// </para>
/// </summary>
public sealed class EngineConfigCredentials
{
    private readonly IReadOnlyList<string> _paths;

    public EngineConfigCredentials(IReadOnlyList<string>? paths = null) => _paths = paths ?? DefaultPaths();

    /// <summary>
    /// Where the engines keep it. Docker's config first, then the containers auth Podman writes — in that
    /// order, so a machine with both prefers the Docker one for a shared host.
    /// </summary>
    private static List<string> DefaultPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string>
        {
            Path.Combine(home, ".docker", "config.json"),
            Path.Combine(home, ".config", "containers", "auth.json"),
        };

        // Podman's usual place is the runtime dir, which is per-session and not under $HOME.
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime))
            paths.Add(Path.Combine(runtime, "containers", "auth.json"));

        return paths;
    }

    /// <summary>Every registry these files hold a login for, without the secrets.</summary>
    public IReadOnlyList<RegistryLogin> List()
    {
        var found = new Dictionary<string, RegistryLogin>(StringComparer.Ordinal);

        foreach (var path in _paths)
        {
            foreach (var login in ParseFile(path))
                found.TryAdd(login.Host, login);             // first file wins; see DefaultPaths
        }

        return [.. found.Values.OrderBy(l => l.Host, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The credential for <paramref name="host"/>, or null when these files hold none. Resolves a
    /// credential helper when the config names one instead of embedding the secret.
    /// </summary>
    public RegistryCredential? Get(string host)
    {
        var wanted = RegistryHost.Canonical(host);

        foreach (var path in _paths)
        {
            var json = ReadText(path);
            if (json is null)
                continue;

            var config = Parse(json);

            // An embedded auth is the simple case: base64 of "user:secret".
            var direct = config.Auths.FirstOrDefault(a => RegistryHost.SameHost(a.Host, wanted));
            if (direct is not null && direct.Secret is not null)
                return new RegistryCredential(wanted, direct.Username ?? string.Empty, direct.Secret);

            // Otherwise a helper holds it. credHelpers wins over credsStore for a specific host, which is
            // the precedence Docker itself applies.
            var helper = config.CredHelpers
                .FirstOrDefault(h => RegistryHost.SameHost(h.Key, wanted)).Value
                ?? config.CredsStore;

            if (string.IsNullOrEmpty(helper))
                continue;

            // The server the helper is keyed by is the one written in the config, not our canonical form:
            // Hub is stored under the legacy v1 URL and asking for "docker.io" finds nothing.
            var server = direct?.Host ?? wanted;
            if (FromHelper(helper, server) is { } fromHelper)
                return fromHelper with { Host = wanted };
        }

        return null;
    }

    private static IEnumerable<RegistryLogin> ParseFile(string path)
    {
        var json = ReadText(path);
        if (json is null)
            return [];

        var config = Parse(json);
        var helperHosts = config.CredHelpers.Keys;

        // A host with only a helper entry still counts as a login: the secret is elsewhere, but the fact
        // that you are logged in there is what the list is for.
        return config.Auths.Select(a => a.Host)
            .Concat(helperHosts)
            .Select(RegistryHost.Canonical)
            .Distinct(StringComparer.Ordinal)
            .Select(host => new RegistryLogin(
                host,
                config.Auths.FirstOrDefault(a => RegistryHost.SameHost(a.Host, host))?.Username ?? string.Empty,
                RegistryCredentialSource.EngineConfig));
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            // Unreadable is the same as absent: this is a convenience, never a requirement.
            return null;
        }
    }

    // ── Parsing, kept pure so it can be tested without a home directory ──────

    /// <summary>One <c>auths</c> entry: the host as written, and whatever could be decoded from it.</summary>
    internal sealed record ConfigAuth(string Host, string? Username, string? Secret);

    internal sealed record EngineConfig(
        IReadOnlyList<ConfigAuth> Auths,
        IReadOnlyDictionary<string, string> CredHelpers,
        string? CredsStore);

    /// <summary>
    /// Reads the shape both Docker and the containers tools use. Anything unexpected is skipped rather
    /// than thrown over: this file is written by other programs and versions, and a surprise in it must
    /// not stop Kontena from starting.
    /// </summary>
    internal static EngineConfig Parse(string json)
    {
        var auths = new List<ConfigAuth>();
        var helpers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? store = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("auths", out var authsNode) && authsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in authsNode.EnumerateObject())
                {
                    string? username = null;
                    string? secret = null;

                    if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (entry.Value.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.String)
                            (username, secret) = DecodeAuth(auth.GetString());

                        // Some writers put these alongside instead of encoding them.
                        if (entry.Value.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String)
                            username ??= user.GetString();
                        if (entry.Value.TryGetProperty("password", out var pass) && pass.ValueKind == JsonValueKind.String)
                            secret ??= pass.GetString();
                    }

                    auths.Add(new ConfigAuth(entry.Name, username, secret));
                }
            }

            if (root.TryGetProperty("credHelpers", out var helperNode) && helperNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in helperNode.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } name)
                        helpers[entry.Name] = name;
                }
            }

            if (root.TryGetProperty("credsStore", out var storeNode) && storeNode.ValueKind == JsonValueKind.String)
                store = storeNode.GetString();
        }
        catch (JsonException)
        {
            // Malformed config: treat as no credentials rather than failing the app.
        }

        return new EngineConfig(auths, helpers, store);
    }

    /// <summary>
    /// Splits the base64 <c>auth</c> field into user and secret. Only the first colon separates them —
    /// a password may contain colons, and splitting on all of them would silently truncate it.
    /// </summary>
    internal static (string? Username, string? Secret) DecodeAuth(string? auth)
    {
        if (string.IsNullOrWhiteSpace(auth))
            return (null, null);

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Trim()));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return colon < 0
                ? (decoded, null)
                : (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    // ── Credential helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="helper"/> may be pasted into an executable name (KON-183).
    /// <para>
    /// The name comes from <c>credsStore</c> or <c>credHelpers</c> in a config file other programs
    /// write. <see cref="Process"/> treats a name containing a separator as a <b>path</b> rather than
    /// a PATH lookup, so <c>x/../../something</c> starts whatever sits there, relative to the working
    /// directory. Real helpers are plain words — <c>desktop</c>, <c>osxkeychain</c>,
    /// <c>secretservice</c>, <c>ecr-login</c> — so accepting only those costs nothing and leaves the
    /// answer where it already was: no credential from here.
    /// </para>
    /// </summary>
    internal static bool IsUsableHelperName(string? helper) =>
        helper is { Length: > 0 } name
        && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// Asks <c>docker-credential-&lt;helper&gt;</c> for a server's credential. The protocol is a
    /// subcommand plus the server on stdin, answered with JSON on stdout — so the secret never appears in
    /// a command line, where another process could read it out of <c>ps</c>.
    /// </summary>
    private static RegistryCredential? FromHelper(string helper, string server)
    {
        if (!IsUsableHelperName(helper))
            return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo($"docker-credential-{helper}", "get")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
                return null;

            process.StandardInput.Write(server);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000) || process.ExitCode != 0)
                return null;

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var username = root.TryGetProperty("Username", out var u) ? u.GetString() : null;
            var secret = root.TryGetProperty("Secret", out var s) ? s.GetString() : null;

            return string.IsNullOrEmpty(secret)
                ? null
                : new RegistryCredential(server, username ?? string.Empty, secret);
        }
        catch (Exception)
        {
            // No such helper on PATH, a helper that refused, unparseable output — all mean "no credential
            // from here", and none of them are worth interrupting a pull for.
            return null;
        }
    }
}
