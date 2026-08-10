using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// Hosts one <see cref="WorkspaceViewModel"/>: folder tree on the left, tabs + editor on the right
/// (KON-287). The folder picker is the one piece of this view nothing here can unit-test — it needs a
/// real <see cref="TopLevel"/> storage provider, same as every other file picker in Kontena
/// (Notes.md "Handmatig testen vóór de tag").
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>
    /// Where the editor's schema comes from (KON-296): the cluster's OpenAPI, or the bundled fallback.
    /// Null leaves the editor unschooled — completion and schema diagnostics simply stay quiet, which
    /// is Plan §3's "unverifiable", not an error.
    /// </summary>
    public static readonly StyledProperty<SchemaIndex?> SchemasProperty =
        AvaloniaProperty.Register<WorkspaceView, SchemaIndex?>(nameof(Schemas));

    /// <summary>The kind the editor's schema currently belongs to, so a keystroke that changes nothing
    /// about apiVersion/kind does not re-ask the cluster.</summary>
    private GroupVersionKind? _schemaKind;

    public WorkspaceView()
    {
        InitializeComponent();

        // Resolve off the editor's own Text rather than off the active tab: it is the same string, and
        // this way a document opened, switched or edited all arrive through one path.
        Editor.GetObservable(ManifestEditorView.TextProperty)
            .Subscribe(new AnonymousObserver<string>(OnDocumentTextChanged));
    }

    public SchemaIndex? Schemas
    {
        get => GetValue(SchemasProperty);
        set => SetValue(SchemasProperty, value);
    }

    /// <summary>
    /// Raised when the user picks a folder, with the workspace that came of it. The view owns the
    /// picker, but not the session: the plan and source-control pages are built beside this one and
    /// need the same workspace (KON-296).
    /// </summary>
    public event EventHandler<WorkspaceViewModel>? WorkspaceOpened;

    private void OnDocumentTextChanged(string text)
    {
        if (Schemas is not { } index)
            return;

        var kind = ManifestKind.Of(text);
        if (kind == _schemaKind)
            return;

        _schemaKind = kind;
        if (kind is null)
        {
            Editor.Schema = null;
            return;
        }

        _ = ResolveSchemaAsync(index, kind.Value);
    }

    private async Task ResolveSchemaAsync(SchemaIndex index, GroupVersionKind kind)
    {
        JsonSchemaNode? schema = null;

        try
        {
            schema = await index.ResolveAsync(kind);
        }
        catch (Exception)
        {
            // A cluster that cannot answer leaves the editor unschooled, exactly like one that does not
            // serve the kind. Plan §5 keeps admission and connectivity out of the editor's authorities:
            // what belongs on screen for a broken connection is the shell's banner, not a squiggle.
        }

        // The document may have moved to another kind while the cluster was answering.
        if (_schemaKind == kind)
            Editor.Schema = schema;
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (path is null)
            return;

        var workspace = new WorkspaceViewModel(ManifestWorkspace.Open(path));
        DataContext = workspace;
        WorkspaceOpened?.Invoke(this, workspace);
    }

    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: WorkspaceNode node } && DataContext is WorkspaceViewModel vm)
            vm.OpenNodeCommand.Execute(node);
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: OpenDocument document } && DataContext is WorkspaceViewModel vm)
            vm.CloseTabCommand.Execute(document);
    }
}
