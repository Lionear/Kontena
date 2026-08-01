using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Kontena.Plugins.ManifestStudio.Schemas;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>Adapts a <see cref="Suggestion"/> to AvaloniaEdit's completion-window contract.</summary>
public sealed class ManifestCompletionData(Suggestion suggestion) : ICompletionData
{
    public IImage? Image => null;

    public string Text => suggestion.Name;

    public object Content => suggestion.Required ? $"{suggestion.Name} (required)" : suggestion.Name;

    public object? Description => suggestion.Description is { Length: > 0 } d ? d : suggestion.Type;

    // Required fields float to the top of AvaloniaEdit's own sort — CompletionEngine already put them
    // first, but the widget re-sorts by priority once selection/filtering kicks in.
    public double Priority => suggestion.Required ? 1 : 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
