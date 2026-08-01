using Kontena.Sdk;

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

    /// <summary>
    /// Create the provider's engine, ping it, and report whether it answered — within the deadline the
    /// provider itself sets (<see cref="IBackendProvider.ProbeTimeout"/>). One deadline for everyone
    /// meant a remote could not pass a probe it had no way of finishing (KON-327).
    /// </summary>
    public static Task<BackendProbe> ProbeAsync(IBackendProvider provider, CancellationToken ct = default)
        => ProbeAsync(provider, provider.ProbeTimeout, ct);

    /// <summary>
    /// As above, with the deadline spelled out — tests need one they do not have to wait for.
    /// </summary>
    public static async Task<BackendProbe> ProbeAsync(
        IBackendProvider provider, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var probe = ConnectAsync(provider, deadline.Token);

        // Race the deadline rather than only cancelling on it. Cancellation is cooperative, and the
        // connect this exists for is the one least likely to cooperate: whether a named pipe honours
        // the token on the way down is the client library's business, and if it does not, awaiting
        // the probe would hand the whole round back to the timeout we are trying to escape.
        var done = await Task.WhenAny(probe, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);

        // The loser keeps running: it owns its backend and disposes it in the finally below. Nothing
        // reads its result, and it cannot fault unobserved — ConnectAsync catches everything.
        return done == probe
            ? await probe.ConfigureAwait(false)
            : new BackendProbe(provider, false, "Not connected");
    }

    private static async Task<BackendProbe> ConnectAsync(IBackendProvider provider, CancellationToken ct)
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
