using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// Wraps AvaloniaEdit's <c>TextEditor</c> behind a bindable <see cref="Text"/> so a tab's
/// <c>OpenDocument.Text</c> can be bound two-way instead of pushed in through code-behind (KON-287),
/// and drives <see cref="CompletionEngine"/>/<see cref="ManifestDiagnostics"/> off <see cref="Schema"/>
/// (KON-290/291) — the pure engines proved themselves in tests; this is where they actually light up
/// the editor. Runtime-verified against the app's pinned Avalonia 12.0.3 in
/// <c>ManifestEditorViewTests</c> (Plans/manifest-studio.md §11) before either was wired on top.
/// </summary>
public partial class ManifestEditorView : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ManifestEditorView, string>(
            nameof(Text), defaultValue: string.Empty, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<JsonSchemaNode?> SchemaProperty =
        AvaloniaProperty.Register<ManifestEditorView, JsonSchemaNode?>(nameof(Schema));

    /// <summary>What the status strip says about this document: "No problems", "2 errors · 1 warning".
    /// Composed here rather than in a binding because the plural of one is not the plural of two, and a
    /// StringFormat cannot know that.</summary>
    public static readonly StyledProperty<string> ProblemSummaryProperty =
        AvaloniaProperty.Register<ManifestEditorView, string>(nameof(ProblemSummary), "No problems");

    public static readonly StyledProperty<bool> HasErrorsProperty =
        AvaloniaProperty.Register<ManifestEditorView, bool>(nameof(HasErrors));

    public static readonly StyledProperty<bool> HasWarningsProperty =
        AvaloniaProperty.Register<ManifestEditorView, bool>(nameof(HasWarnings));

    public static readonly StyledProperty<bool> HasProblemsProperty =
        AvaloniaProperty.Register<ManifestEditorView, bool>(nameof(HasProblems));

    // Guards against the two-way Text binding re-entering itself: setting Editor.Text from a Text
    // change fires Editor.TextChanged, which would otherwise write Text right back and loop.
    private bool _syncing;

    private CompletionWindow? _completionWindow;
    private IReadOnlyList<Diagnostic> _diagnostics = [];

    public ManifestEditorView()
    {
        InitializeComponent();
        Editor.TextArea.TextView.BackgroundRenderers.Add(new DiagnosticRenderer(() => _diagnostics));

        // Two spaces, because every manifest in the world is written that way and a tab in YAML is a
        // syntax error rather than a preference.
        Editor.Options.IndentationSize = 2;
        Editor.Options.ConvertTabsToSpaces = true;

        ApplyHighlighting();
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();

        Editor.TextChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _syncing = true;
                SetCurrentValue(TextProperty, Editor.Text);
                _syncing = false;
            }

            RecomputeDiagnostics();
        };

        Editor.TextArea.TextEntered += OnTextEntered;
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The resolved root schema for whatever kind the document declares. Null means
    /// unverifiable (Plan §3) — completion offers nothing and diagnostics stay silent, rather than
    /// either engine guessing.</summary>
    public JsonSchemaNode? Schema
    {
        get => GetValue(SchemaProperty);
        set => SetValue(SchemaProperty, value);
    }

    public string ProblemSummary
    {
        get => GetValue(ProblemSummaryProperty);
        private set => SetValue(ProblemSummaryProperty, value);
    }

    public bool HasErrors
    {
        get => GetValue(HasErrorsProperty);
        private set => SetValue(HasErrorsProperty, value);
    }

    public bool HasWarnings
    {
        get => GetValue(HasWarningsProperty);
        private set => SetValue(HasWarningsProperty, value);
    }

    /// <summary>Whether <see cref="Problems"/> has anything in it. A bindable property rather than a
    /// count the pane negates, because "no problems found" and "nothing was checked" are different
    /// sentences and the pane has to be able to pick one.</summary>
    public bool HasProblems
    {
        get => GetValue(HasProblemsProperty);
        private set => SetValue(HasProblemsProperty, value);
    }

    internal CompletionWindow? CompletionWindow => _completionWindow;

    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// The same findings the squiggles are drawn from, as rows a Problems pane can list (KON-427) with
    /// whatever <see cref="QuickFixEngine"/> offers for each. Until now the diagnostics only ever
    /// reached the screen as an underline: you could see that a line was wrong without being told what
    /// was wrong with it, and KON-292's fixes were computed by nothing at all.
    /// </summary>
    public ObservableCollection<Problem> Problems { get; } = [];

    /// <summary>Applies a quick fix to the document. Line-range replacement, so everything outside the
    /// edited lines — comments, ordering, unrelated fields — is untouched (Plan §5), and it lands as one
    /// undo step rather than as a rewrite of the file.</summary>
    public void ApplyFix(QuickFix fix)
    {
        var document = Editor.Document;
        if (fix.Edit.StartLine < 0 || fix.Edit.StartLine >= document.LineCount)
            return;

        var first = document.GetLineByNumber(fix.Edit.StartLine + 1);
        var lastLine = Math.Min(fix.Edit.EndLine, document.LineCount);
        var last = document.GetLineByNumber(lastLine);

        // TotalLength rather than Length on the final line, so a pure deletion takes its newline with it
        // and does not leave a blank line where the field was.
        var end = fix.Edit.NewLines.Count == 0 ? last.Offset + last.TotalLength : last.EndOffset;
        var replacement = string.Join(Environment.NewLine, fix.Edit.NewLines);

        document.Replace(first.Offset, end - first.Offset, replacement);
    }

    private void ApplyHighlighting() => Editor.SyntaxHighlighting = YamlHighlighting.For(ActualThemeVariant);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_syncing)
        {
            _syncing = true;
            Editor.Text = change.GetNewValue<string>() ?? string.Empty;
            _syncing = false;
        }

        if (change.Property == SchemaProperty)
            RecomputeDiagnostics();
    }

    private void RecomputeDiagnostics()
    {
        _diagnostics = SingleDocumentDiagnostics.Validate(Editor.Text, Schema);

        Problems.Clear();
        foreach (var diagnostic in _diagnostics)
            Problems.Add(new Problem(diagnostic, QuickFixEngine.Suggest(diagnostic, Editor.Text)));

        var errors = _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var hints = _diagnostics.Count - errors - warnings;

        HasErrors = errors > 0;
        HasWarnings = warnings > 0;
        HasProblems = _diagnostics.Count > 0;
        ProblemSummary = Summarise(errors, warnings, hints);

        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>"No problems" is a claim about a document the editor could check. A document whose kind
    /// the cluster does not serve has no schema at all, and saying it is clean would be the false
    /// certainty Plan §3 keeps out of this editor — so it says it did not look.</summary>
    private string Summarise(int errors, int warnings, int hints)
    {
        if (Schema is null)
            return "No schema for this kind";

        if (errors + warnings + hints == 0)
            return "No problems";

        var parts = new List<string>(3);
        if (errors > 0)
            parts.Add(errors == 1 ? "1 error" : $"{errors} errors");
        if (warnings > 0)
            parts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
        if (hints > 0)
            parts.Add(hints == 1 ? "1 hint" : $"{hints} hints");

        return string.Join(" · ", parts);
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        var suggestions = CompletionEngine.Suggest(Editor.Text, Editor.CaretOffset, Schema);

        if (suggestions.Count == 0)
        {
            _completionWindow?.Close();
            return;
        }

        if (_completionWindow is null)
        {
            _completionWindow = new CompletionWindow(Editor.TextArea) { CompletionList = { IsFiltering = false } };
            _completionWindow.Closed += (_, _) => _completionWindow = null;
        }

        _completionWindow.StartOffset = WordStart(Editor.Text, Editor.CaretOffset);
        _completionWindow.EndOffset = Editor.CaretOffset;

        _completionWindow.CompletionList.CompletionData.Clear();
        foreach (var suggestion in suggestions)
            _completionWindow.CompletionList.CompletionData.Add(new ManifestCompletionData(suggestion));

        _completionWindow.Show();
    }

    // CompletionEngine already filtered by whatever was typed since the start of the current word — the
    // window is told not to re-filter (IsFiltering = false above), but it still needs to know where
    // that word started, so accepting a suggestion replaces the partial text instead of inserting next
    // to it.
    private static int WordStart(string text, int caret)
    {
        var i = caret;
        while (i > 0 && IsWordChar(text[i - 1]))
            i--;

        return i;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '.';
}
