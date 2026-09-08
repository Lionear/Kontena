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

    /// <summary>
    /// The one directory Kontena keeps its own data in — settings, plugins, managed tool copies, the
    /// diagnostics log, an interrupted rollout. Everything that writes under the platform's
    /// application-data directory hangs off this, so there is a single place to say where that is.
    /// <para>
    /// A debug build gets a directory of its own (KON-421). A <c>dotnet run</c> from a working copy
    /// used to compute exactly the same path as the installed app, so testing a change rewrote the
    /// developer's real settings.json — which happened twice. The separation is the build
    /// configuration rather than an environment variable on purpose: a variable only protects the
    /// runs where somebody remembered to set it.
    /// </para>
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear",
#if DEBUG
        Name + "-Dev");
#else
        Name);
#endif
}
