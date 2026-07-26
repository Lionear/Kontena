namespace Kontena.Core.Tooling;

/// <summary>Raised when a tool Kontena wanted to run is not installed.</summary>
public sealed class ToolNotFoundException(string tool)
    : Exception($"'{tool}' was not found on this machine.")
{
    public string Tool { get; } = tool;
}
