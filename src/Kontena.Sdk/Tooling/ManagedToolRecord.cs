namespace Kontena.Sdk.Tooling;

/// <summary>
/// What Kontena recorded about a copy it installed itself: where it is, what it hashed to, and which
/// release it came from. The version matters because no package manager is watching this one.
/// </summary>
public sealed record ManagedToolRecord(ExternalTool Tool, string Path, string Sha256, string Version);
