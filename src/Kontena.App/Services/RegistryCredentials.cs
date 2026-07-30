using Kontena.Sdk.Models;
using Kontena.Core.Models;

namespace Kontena.App.Services;

/// <summary>
/// Finds the login for an image, from the two places one can come from (KON-114).
/// <para>
/// Kontena's own logins win over the engine's config. Both are legitimate, but one of them is something
/// the user did *in this app*, deliberately and more recently — if they typed a login here, that is the
/// account they mean, whatever a config file from last year says.
/// </para>
/// </summary>
public sealed class RegistryCredentials
{
    private readonly ISecretStore _secrets;
    private readonly EngineConfigCredentials _engineConfig;
    private readonly Func<KontenaSettings> _settings;

    public RegistryCredentials(
        ISecretStore secrets, Func<KontenaSettings> settings, EngineConfigCredentials? engineConfig = null)
    {
        _secrets = secrets;
        _settings = settings;
        _engineConfig = engineConfig ?? new EngineConfigCredentials();
    }

    /// <summary>
    /// The credential to pull <paramref name="reference"/> with, or null to pull anonymously — which is
    /// the right answer for a public image and must not become an error.
    /// </summary>
    public async ValueTask<RegistryCredential?> ForAsync(string reference, CancellationToken ct = default)
    {
        var host = RegistryHost.For(reference);

        var stored = _settings().Registries
            .FirstOrDefault(r => RegistryHost.SameHost(r.Host, host));

        if (stored is not null)
        {
            var secret = await _secrets.GetAsync(SecretKeys.Registry(stored.Host), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(secret))
                return new RegistryCredential(stored.Host, stored.Username, secret);

            // Listed but the keychain has nothing: the entry was revoked outside Kontena, or the keychain
            // is locked. Falling through to the engine's config is better than failing outright, and
            // better than sending an empty password.
        }

        return _engineConfig.Get(host);
    }

    /// <summary>
    /// Every registry a login is known for: Kontena's own, then anything inherited from the engine's
    /// config that is not already covered. The list says which is which — "why does this work when I
    /// never logged in here?" is a fair question.
    /// </summary>
    public IReadOnlyList<RegistryLogin> List()
    {
        var own = _settings().Registries;
        var inherited = _engineConfig.List()
            .Where(i => !own.Any(o => RegistryHost.SameHost(o.Host, i.Host)));

        return [.. own.Concat(inherited).OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase)];
    }
}
