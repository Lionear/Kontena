using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Tests.Schemas;

public sealed class QuickFixEngineTests
{
    [Fact]
    public void An_unknown_leaf_field_is_removed_as_a_single_line()
    {
        const string document = "apiVersion: apps/v1\nkind: Deployment\nspec:\n  bogus: yes\n";
        var diagnostic = new Diagnostic(DiagnosticAuthority.Schema, DiagnosticSeverity.Error, 3, "Unknown field 'bogus'.")
        {
            Kind = DiagnosticKind.UnknownField, FieldName = "bogus",
        };

        var fix = Assert.Single(QuickFixEngine.Suggest(diagnostic, document));

        Assert.Equal("Remove 'bogus'", fix.Title);
        Assert.Equal(3, fix.Edit.StartLine);
        Assert.Equal(4, fix.Edit.EndLine);
        Assert.Empty(fix.Edit.NewLines);
    }

    [Fact]
    public void An_unknown_block_field_removes_its_whole_nested_span()
    {
        const string document = """
            apiVersion: apps/v1
            kind: Deployment
            spec:
              bogus:
                nested: true
                more: 1
              replicas: 3
            """;
        var diagnostic = new Diagnostic(DiagnosticAuthority.Schema, DiagnosticSeverity.Error, 3, "Unknown field 'bogus'.")
        {
            Kind = DiagnosticKind.UnknownField, FieldName = "bogus",
        };

        var fix = Assert.Single(QuickFixEngine.Suggest(diagnostic, document));

        // Lines 3-5: "bogus:", "nested: true", "more: 1" — the whole subtree, stopping before
        // "replicas: 3" at line 6, which is bogus's sibling, not its child.
        Assert.Equal(3, fix.Edit.StartLine);
        Assert.Equal(6, fix.Edit.EndLine);
    }

    [Fact]
    public void A_deprecated_core_apiVersion_is_updated_preserving_indentation()
    {
        const string document = "apiVersion: v1beta9\nkind: Deployment\n";
        var diagnostic = new Diagnostic(DiagnosticAuthority.ClusterDiscovery, DiagnosticSeverity.Warning, 0, "irrelevant")
        {
            Kind = DiagnosticKind.DeprecatedApiVersion, SuggestedVersion = "v1",
        };

        var fix = Assert.Single(QuickFixEngine.Suggest(diagnostic, document));

        Assert.Equal("Update apiVersion to v1", fix.Title);
        Assert.Equal(0, fix.Edit.StartLine);
        Assert.Equal(1, fix.Edit.EndLine);
        Assert.Equal(["apiVersion: v1"], fix.Edit.NewLines);
    }

    [Fact]
    public void A_deprecated_grouped_apiVersion_keeps_its_group()
    {
        const string document = "apiVersion: apps/v1beta9\nkind: Deployment\n";
        var diagnostic = new Diagnostic(DiagnosticAuthority.ClusterDiscovery, DiagnosticSeverity.Warning, 0, "irrelevant")
        {
            Kind = DiagnosticKind.DeprecatedApiVersion, SuggestedVersion = "v1",
        };

        var fix = Assert.Single(QuickFixEngine.Suggest(diagnostic, document));

        Assert.Equal(["apiVersion: apps/v1"], fix.Edit.NewLines);
    }

    [Theory]
    [InlineData(DiagnosticKind.MissingRequiredField)]
    [InlineData(DiagnosticKind.WrongType)]
    [InlineData(DiagnosticKind.UnmatchedReference)]
    [InlineData(DiagnosticKind.Other)]
    public void No_fix_is_offered_for_kinds_this_engine_does_not_handle(DiagnosticKind kind)
    {
        var diagnostic = new Diagnostic(DiagnosticAuthority.Schema, DiagnosticSeverity.Error, 0, "irrelevant")
        {
            Kind = kind, FieldName = "x", SuggestedVersion = "v1",
        };

        Assert.Empty(QuickFixEngine.Suggest(diagnostic, "apiVersion: v1\n"));
    }
}
