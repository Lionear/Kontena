namespace Kontena.Sdk;

/// <summary>Result of probing a provider for availability.</summary>
/// <param name="Provider">The provider that was probed.</param>
/// <param name="Connected">True when the backend answered a ping.</param>
/// <param name="Detail">Version/endpoint when connected, or a short reason otherwise.</param>
public sealed record BackendProbe(IBackendProvider Provider, bool Connected, string? Detail);
