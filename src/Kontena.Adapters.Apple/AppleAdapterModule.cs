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

        // The only bundled adapter with a platform floor, and it has to say so: an empty list means
        // "anywhere" (PluginPlatform.SupportsHost), so leaving this out is what offers Apple's runtime
        // on Windows and Linux. The floor is 26 because that is the release `container` ships with —
        // an older macOS has no such binary to drive (KON-429).
        Platforms = [new PluginPlatform { Os = "macos", MinVersion = "26" }],
    };
}
