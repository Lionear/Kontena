namespace Kontena.Sdk.Tooling;

/// <summary>
/// Raised when downloaded bytes do not match the digest the publisher announced. Deliberately not a
/// warning: at this point the only honest thing to do is throw the file away.
/// </summary>
public sealed class ToolVerificationException(string tool, string expected, string actual)
    : Exception($"The download for '{tool}' did not match its published checksum. " +
                $"Expected {expected}, got {actual}. The file has been discarded.")
{
    public string Tool { get; } = tool;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}
