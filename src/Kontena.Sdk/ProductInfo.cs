namespace Kontena.Sdk;

/// <summary>
/// Central product identity. The abstraction layer, adapters and UI all
/// share this so the name and versioning live in exactly one place.
/// </summary>
public static class ProductInfo
{
    /// <summary>Human-facing product name.</summary>
    public const string Name = "Kontena";

    /// <summary>Short tagline used in about screens and logs.</summary>
    public const string Tagline = "One UI. Any engine.";
}
