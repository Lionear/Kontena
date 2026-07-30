using Kontena.Sdk;

namespace Kontena.Engines;

/// <summary>
/// Placeholder anchor for the engine-abstraction layer. The real Container
/// Engine Abstraction Layer (CEAL) contract lands in KON-20; this exists so
/// the assembly, namespace and the reference to <see cref="Kontena.Sdk"/>
/// are wired and verified by the scaffold.
/// </summary>
public static class EnginesInfo
{
    /// <summary>Short description proving the Core reference resolves.</summary>
    public static string Describe() => $"{ProductInfo.Name} engine layer";
}
