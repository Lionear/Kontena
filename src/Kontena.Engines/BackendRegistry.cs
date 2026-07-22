using Kontena.Core;

namespace Kontena.Engines;

/// <summary>
/// Holds the known backend providers and probes them for availability. The app uses this
/// instead of hard-coding backends, which makes it provider-based and ready for both cluster
/// providers (the OAL, later) and store-installed adapters (a future plugin loader just calls
/// <see cref="Register"/>).
/// </summary>
public sealed class BackendRegistry
{
    private readonly List<IBackendProvider> _providers;

    public BackendRegistry(IEnumerable<IBackendProvider> providers)
        => _providers = providers.ToList();

    public IReadOnlyList<IBackendProvider> Providers => _providers;

    /// <summary>Add a provider (e.g. contributed by a plugin at runtime).</summary>
    public void Register(IBackendProvider provider) => _providers.Add(provider);

    /// <summary>
    /// Swap the whole provider set — used when the set itself changes at runtime, e.g. switching the
    /// demo backends off. Callers re-probe afterwards, since the previous probes describe providers
    /// that may no longer be here.
    /// </summary>
    public void Replace(IEnumerable<IBackendProvider> providers)
    {
        _providers.Clear();
        _providers.AddRange(providers);
    }

    /// <summary>Probe every provider concurrently.</summary>
    public async Task<IReadOnlyList<BackendProbe>> ProbeAllAsync(CancellationToken ct = default)
        => await Task.WhenAll(_providers.Select(p => ProbeAsync(p, ct))).ConfigureAwait(false);

    /// <summary>Create the provider's engine, ping it, and report whether it answered.</summary>
    public static async Task<BackendProbe> ProbeAsync(IBackendProvider provider, CancellationToken ct = default)
    {
        IBackend? backend = null;
        try
        {
            backend = provider.CreateBackend();
            await backend.PingAsync(ct).ConfigureAwait(false);
            var info = await backend.GetInfoAsync(ct).ConfigureAwait(false);
            var detail = string.IsNullOrEmpty(info.Version) ? info.Endpoint : $"{info.Version} · {info.Endpoint}";
            return new BackendProbe(provider, true, detail);
        }
        catch
        {
            return new BackendProbe(provider, false, "Not connected");
        }
        finally
        {
            (backend as IDisposable)?.Dispose();
        }
    }
}
