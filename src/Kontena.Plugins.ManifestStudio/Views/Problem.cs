using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// One row of the Problems pane (KON-427): a diagnostic, the quick fixes <see cref="QuickFixEngine"/>
/// offers for it, and the few strings the list needs to say where it came from.
/// <para>
/// The authority is on screen next to every finding on purpose — Plan §5 is that schema, cluster
/// discovery, cross-document and policy never speak for one another, and a list that renders them
/// identically is exactly that collapse. Severity is carried by a glyph and a word, never by colour
/// alone (DesignSystem.md §Accessibility).
/// </para>
/// </summary>
public sealed record Problem(Diagnostic Diagnostic, IReadOnlyList<QuickFix> Fixes)
{
    public string Message => Diagnostic.Message;

    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;
    public bool IsHint => Diagnostic.Severity == DiagnosticSeverity.Hint;

    /// <summary>"line 10 · schema" — the line, then which authority said so.</summary>
    public string Location => $"line {Diagnostic.Line + 1} · {AuthorityLabel}";

    /// <summary>The first fix's title, or null when nothing is offered. One fix per finding is all the
    /// engine ever returns today, and a pane 288px wide is no place for a menu.</summary>
    public string? FixTitle => Fixes.Count > 0 ? Fixes[0].Title : null;

    public bool HasFix => Fixes.Count > 0;

    private string AuthorityLabel => Diagnostic.Authority switch
    {
        DiagnosticAuthority.Schema => "schema",
        DiagnosticAuthority.ClusterDiscovery => "cluster discovery",
        DiagnosticAuthority.CrossDocument => "cross-document",
        DiagnosticAuthority.Policy => "policy",
        _ => "unknown",
    };

    /// <summary>Spoken by screen readers instead of the glyph, which is decoration beside this text
    /// (DesignSystem.md §Accessibility).</summary>
    public string SeverityLabel => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Warning => "Warning",
        _ => "Hint",
    };
}
