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

    /// <summary>Probe every provider concurrently.</summary>
    public async Task<IReadOnlyList<BackendProbe>> ProbeAllAsync(CancellationToken ct = default)
        => await Task.WhenAll(_providers.Select(p => ProbeAsync(p, ct))).ConfigureAwait(false);

    /// <summary>Create the provider's engine, ping it, and report whether it answered.</summary>
    public static async Task<BackendProbe> ProbeAsync(IBackendProvider provider, CancellationToken ct = default)
    {
        IContainerEngine? engine = null;
        try
        {
            engine = provider.CreateEngine();
            await engine.PingAsync(ct).ConfigureAwait(false);
            var info = await engine.GetInfoAsync(ct).ConfigureAwait(false);
            var detail = string.IsNullOrEmpty(info.Version) ? info.Endpoint : $"{info.Version} · {info.Endpoint}";
            return new BackendProbe(provider, true, detail);
        }
        catch
        {
            return new BackendProbe(provider, false, "Not connected");
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
        }
    }
}
