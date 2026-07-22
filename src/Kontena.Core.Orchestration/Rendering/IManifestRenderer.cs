namespace Kontena.Core.Orchestration.Rendering;

/// <summary>How serious a render finding is.</summary>
public enum RenderSeverity
{
    /// <summary>Worth knowing — resource counts, what the tool skipped.</summary>
    Info,

    /// <summary>Suspicious but renderable — a document without a name, a lint complaint.</summary>
    Warning,

    /// <summary>The render failed, or produced something that cannot be applied.</summary>
    Error,
}

/// <summary>One finding from a render or a static check, in the tool's own words where possible.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong, ready to show.</param>
/// <param name="Source">Where it came from — "kustomize", "helm lint", "check".</param>
public sealed record RenderDiagnostic(RenderSeverity Severity, string Message, string Source = "");

/// <summary>What every render is given, whatever the source shape.</summary>
public abstract record RenderRequest
{
    /// <summary>Namespace to render into; documents that set their own keep it.</summary>
    public string? Namespace { get; init; }
}

/// <summary>
/// The outcome of a render: flat multi-doc YAML for the declarative core, plus everything that
/// was noticed on the way there. A failed render still carries diagnostics — that is the point.
/// </summary>
public sealed record RenderResult
{
    /// <summary>The rendered bundle: one or more <c>---</c>-separated documents.</summary>
    public string Yaml { get; init; } = string.Empty;

    /// <summary>Findings from the tool and from the static checks, worst first.</summary>
    public IReadOnlyList<RenderDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The command that produced this, verbatim — a render should be reproducible in a terminal.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>How many documents came out.</summary>
    public int DocumentCount { get; init; }

    /// <summary>True when there is something worth applying and nothing fatal was found.</summary>
    public bool Ok => Yaml.Length > 0 && !HasErrors;

    public bool HasErrors => Diagnostics.Any(d => d.Severity == RenderSeverity.Error);

    /// <summary>A render that never got off the ground.</summary>
    public static RenderResult Failed(string command, params string[] messages) => new()
    {
        Command = command,
        Diagnostics = [.. messages.Select(m => new RenderDiagnostic(RenderSeverity.Error, m))],
    };
}

/// <summary>
/// Something that turns a source tree into manifests. Rendering never touches the cluster —
/// validating against a live API server is the dry-run's job (KON-86). This stage only has to
/// produce YAML and be honest about what went wrong, so it also works offline as a lint.
/// </summary>
public interface IManifestRenderer
{
    /// <summary>Display name — "Kustomize", "Helm".</summary>
    string Name { get; }

    /// <summary>The CLI this renderer drives, resolved against PATH; null when it isn't installed.</summary>
    string? Locate();
}

/// <summary>A renderer for one shape of request. Generic so each source keeps its own inputs.</summary>
public interface IManifestRenderer<in TRequest> : IManifestRenderer
    where TRequest : RenderRequest
{
    ValueTask<RenderResult> RenderAsync(TRequest request, CancellationToken ct = default);
}
