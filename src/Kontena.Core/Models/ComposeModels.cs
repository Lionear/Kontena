namespace Kontena.Core.Models;

/// <summary>Request to bring a Compose project up from a compose file.</summary>
public sealed record ComposeUpRequest
{
    /// <summary>Path to the compose file (e.g. <c>docker-compose.yml</c> / <c>compose.yaml</c>).</summary>
    public required string ComposeFilePath { get; init; }

    /// <summary>Project name; the engine defaults to the compose file's directory name when null.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Build images before starting, matching <c>--build</c>.</summary>
    public bool Build { get; init; }

    /// <summary>Recreate containers even when their config is unchanged (<c>--force-recreate</c>).</summary>
    public bool ForceRecreate { get; init; }
}

/// <summary>A single streamed output line from a Compose operation.</summary>
/// <param name="Text">Human-readable toolchain output line.</param>
/// <param name="Error">Non-null when the line represents a failure (carries a short code/reason).</param>
public sealed record ComposeProgress(string Text, string? Error = null);
