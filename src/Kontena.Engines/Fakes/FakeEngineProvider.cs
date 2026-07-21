using Kontena.Core;

namespace Kontena.Engines.Fakes;

/// <summary>Provider for the in-memory <see cref="FakeEngine"/> (dev/demo fallback).</summary>
public sealed class FakeEngineProvider : IBackendProvider
{
    private readonly string _backend;
    private readonly string _displayName;
    private readonly string _chip;

    /// <summary>
    /// Defaults to the "fake" identity used everywhere in the app. The optional identity override
    /// exists only for the screenshot renderer, which presents the demo seed under a real engine's
    /// name/chip — it never affects the shipped app, which always constructs this with the defaults.
    /// </summary>
    public FakeEngineProvider(string backend = "fake", string displayName = "Fake engine", string chip = "F")
    {
        _backend = backend;
        _displayName = displayName;
        _chip = chip;
    }

    public string Backend => _backend;
    public string DisplayName => _displayName;
    public string Chip => _chip;
    public BackendKind Kind => BackendKind.Engine;
    public IBackend CreateBackend() => new FakeEngine(backend: _backend, displayName: _displayName);
}
