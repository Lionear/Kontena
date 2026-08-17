using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Core.Orchestration.Export;

/// <summary>What a sink did, as one value a view can switch on.</summary>
public enum SinkOutcome
{
    /// <summary>The file was written, and nothing else was asked of the sink.</summary>
    Written,

    /// <summary>The file was written and the kustomization now lists it.</summary>
    Registered,

    /// <summary>
    /// The file was written, but it is not listed anywhere yet — the tool that owns the index was
    /// missing or refused. <see cref="SinkResult.FallbackLine"/> is the line to add by hand.
    /// </summary>
    NotRegistered,

    /// <summary>Nothing was written, on purpose. <see cref="SinkResult.Message"/> says why.</summary>
    Refused,

    /// <summary>Nothing was written, because the file system said no.</summary>
    Failed,
}

/// <summary>
/// The outcome of an export: where it landed, what was run, and — when only half of it could be
/// done — the exact line the user has to add themselves.
/// </summary>
public sealed record SinkResult
{
    /// <summary>What happened.</summary>
    public required SinkOutcome Outcome { get; init; }

    /// <summary>The absolute path of the file, once there is one to name.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// The command that was run, verbatim, including the <c>cd</c> it needs — an export should be
    /// reproducible in a terminal, the same discipline
    /// <see cref="Rendering.RenderResult.Command"/> keeps. Empty for a sink that runs nothing.
    /// </summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>
    /// The <c>resources:</c> entry for this file, indented and ready to paste, with the path
    /// relative to the kustomization rather than absolute. Only set where an index exists.
    /// </summary>
    public string FallbackLine { get; init; } = string.Empty;

    /// <summary>Why it was refused, or what is still missing — ready to show.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>True when everything that was asked for happened.</summary>
    public bool Ok => Outcome is SinkOutcome.Written or SinkOutcome.Registered;

    /// <summary>An export that was declined before anything was written.</summary>
    public static SinkResult Refused(string message, string path = "")
        => new() { Outcome = SinkOutcome.Refused, Message = message, Path = path };
}

/// <summary>
/// Somewhere a rendered bundle can be saved: a directory, a kustomization, later a repository.
/// Exporting never touches a cluster — this is the other half of what an authored manifest is for,
/// for people whose clusters are fed by GitOps rather than by <c>apply</c>.
/// <para>
/// A sink writes and reports; it never asks. Whether an existing file may be replaced, and what to
/// do when the tool that owns an index is missing, are decisions the implementation states up front
/// rather than choices it makes per call.
/// </para>
/// </summary>
public interface IManifestSink
{
    ValueTask<SinkResult> WriteAsync(ManifestBundle bundle, CancellationToken ct = default);
}
