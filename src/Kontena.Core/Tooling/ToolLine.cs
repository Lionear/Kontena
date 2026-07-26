namespace Kontena.Core.Tooling;

/// <summary>Which of the two output streams a line came from.</summary>
public enum ToolOutputKind
{
    /// <summary>Standard output.</summary>
    Out,

    /// <summary>Standard error. Not the same as "an error" — many tools report progress here.</summary>
    Error,
}

/// <summary>
/// One line of a running tool's output, in the order it arrived. Both streams are interleaved into a
/// single sequence deliberately: a console that shows stdout and stderr apart loses the ordering that
/// makes the output readable, and tools like kind write their progress to stderr.
/// </summary>
public readonly record struct ToolLine(ToolOutputKind Stream, string Text);
