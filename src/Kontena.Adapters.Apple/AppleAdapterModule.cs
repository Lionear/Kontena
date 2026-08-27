using Kontena.Sdk;

namespace Kontena.Adapters.Apple;

/// <summary>Anchor for the Apple <c>container</c> adapter (KON-31) — the CEAL against Apple's own runtime.</summary>
public static class AppleAdapterModule
{
    /// <summary>Backend identifier used by the engine registry.</summary>
    public const string BackendId = "apple";

    /// <summary>How this adapter describes itself in Settings › Extensions (KON-283).</summary>
    public static EngineManifest Manifest { get; } = new()
    {
        Id = BackendId,
        Name = "Apple container",
        Version = "1.0",
        Author = "Kontena",
        Description =
            "Apple's native container runtime, which ships with macOS 26 and runs each container in "
            + "its own lightweight VM.",
    };
}
