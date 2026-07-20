namespace Kontena.Sdk;

/// <summary>Metadata for a Kontena extension, used by the (future) plugin loader and store.</summary>
public sealed record EngineManifest
{
    /// <summary>Stable unique id, e.g. "com.acme.nomad".</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name.</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version of the extension.</summary>
    public required string Version { get; init; }

    /// <summary>Author or vendor.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Short description shown in the store.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Minimum Kontena SDK version this extension targets.</summary>
    public string MinSdkVersion { get; init; } = string.Empty;
}
