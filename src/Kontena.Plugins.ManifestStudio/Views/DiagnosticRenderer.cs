using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// Underlines each diagnostic's line in its severity's colour — a straight line, not a squiggle. The
/// mockup already spent an afternoon learning that "wavy" is a <c>text-decoration</c>, not a border
/// style (Plan §12); real squiggle geometry is a follow-up polish item, not what proves this wiring
/// works. Reads live off <paramref name="getDiagnostics"/> rather than a snapshot, so the caller only
/// has to call <c>TextView.Redraw()</c> after recomputing — nothing here needs telling twice.
/// </summary>
public sealed class DiagnosticRenderer(Func<IReadOnlyList<Diagnostic>> getDiagnostics) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, Avalonia.Media.DrawingContext drawingContext)
    {
        var document = textView.Document;
        if (document is null)
            return;

        foreach (var diagnostic in getDiagnostics())
        {
            var lineNumber = diagnostic.Line + 1; // Diagnostic.Line is 0-based; AvaloniaEdit lines are 1-based.
            if (lineNumber < 1 || lineNumber > document.LineCount)
                continue;

            var pen = new Pen(new SolidColorBrush(ColorFor(diagnostic.Severity)), 2);
            var line = document.GetLineByNumber(lineNumber);

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line, false))
                drawingContext.DrawLine(pen, rect.BottomLeft, rect.BottomRight);
        }
    }

    private static Color ColorFor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => Colors.Crimson,
        DiagnosticSeverity.Warning => Colors.Orange,
        _ => Colors.Gray,
    };
}
