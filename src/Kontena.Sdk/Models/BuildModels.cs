namespace Kontena.Sdk.Models;

/// <summary>Describes an image build from a Dockerfile in a local context directory.</summary>
public sealed record BuildRequest
{
    /// <summary>Absolute path to the build context directory.</summary>
    public required string ContextPath { get; init; }

    /// <summary>Dockerfile path relative to the context (e.g. "Dockerfile").</summary>
    public string Dockerfile { get; init; } = "Dockerfile";

    /// <summary>Tag to apply to the built image (e.g. "myapp:1.0").</summary>
    public required string Tag { get; init; }

    /// <summary>Target build stage, for multi-stage Dockerfiles; null builds the final stage.</summary>
    public string? Target { get; init; }

    /// <summary>Build without using the layer cache.</summary>
    public bool NoCache { get; init; }

    /// <summary>Always attempt to pull newer base images.</summary>
    public bool Pull { get; init; } = true;

    /// <summary>Build-time arguments (ARG name → value).</summary>
    public IReadOnlyDictionary<string, string> BuildArgs { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>A single line of builder output while an image builds.</summary>
/// <param name="Text">The output line (a "Step N/M : …" header, a log line, or status).</param>
/// <param name="Error">Non-null when the build failed, carrying the failure message.</param>
public sealed record BuildProgress(string Text, string? Error = null);
