using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;
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

    // Guards against the two-way Text binding re-entering itself: setting Editor.Text from a Text
    // change fires Editor.TextChanged, which would otherwise write Text right back and loop.
    private bool _syncing;

    private CompletionWindow? _completionWindow;
    private IReadOnlyList<Diagnostic> _diagnostics = [];

    public ManifestEditorView()
    {
        InitializeComponent();
        Editor.TextArea.TextView.BackgroundRenderers.Add(new DiagnosticRenderer(() => _diagnostics));

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

    internal CompletionWindow? CompletionWindow => _completionWindow;

    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

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
        Editor.TextArea.TextView.Redraw();
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
