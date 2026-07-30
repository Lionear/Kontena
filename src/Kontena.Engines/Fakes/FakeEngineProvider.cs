using Kontena.Core;

namespace Kontena.Engines.Fakes;

/// <summary>Provider for the in-memory <see cref="FakeEngine"/> (dev/demo fallback).</summary>
public sealed class FakeEngineProvider : IBackendProvider
{
    private readonly string _backend;
    private readonly string _displayName;
    private readonly string _chip;
    private readonly BackendChipStyle? _chipStyle;

    /// <summary>
    /// Defaults to the "fake" identity used everywhere in the app. The optional identity override
    /// exists only for the screenshot renderer, which presents the demo seed under a real engine's
    /// name/chip — it never affects the shipped app, which always constructs this with the defaults.
    /// </summary>
    /// <param name="chipStyle">
    /// A mark to wear instead of the letter (KON-80). Null by default, which is what the app's own demo
    /// backend uses: a fake engine should not carry a real product's logo. The renderer passes one
    /// because its whole purpose is to look like the engine it is standing in for.
    /// </param>
    public FakeEngineProvider(string backend = "fake", string displayName = "Fake engine", string chip = "F",
        BackendChipStyle? chipStyle = null)
    {
        _backend = backend;
        _displayName = displayName;
        _chip = chip;
        _chipStyle = chipStyle;
    }

    public string Backend => _backend;
    public string DisplayName => _displayName;
    public string Chip => _chip;
    public BackendChipStyle? ChipStyle => _chipStyle;
    public BackendKind Kind => BackendKind.Engine;
    public IBackend CreateBackend() => new FakeEngine(backend: _backend, displayName: _displayName);
}
