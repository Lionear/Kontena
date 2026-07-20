namespace Kontena.Engines.Fakes;

/// <summary>Provider for the in-memory <see cref="FakeEngine"/> (dev/demo fallback).</summary>
public sealed class FakeEngineProvider : IEngineProvider
{
    public string Backend => "fake";
    public string DisplayName => "Fake engine";
    public string Chip => "F";
    public IContainerEngine CreateEngine() => new FakeEngine();
}
