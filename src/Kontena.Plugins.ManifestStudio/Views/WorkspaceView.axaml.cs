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

    /// <summary>
    /// Whether the schemas behind the squiggles came from the connected cluster or from the bundled
    /// fallback (KON-427). The breadcrumb says which, because "no errors" from a bundled schema set that
    /// has never heard of your CRD is a different statement from "no errors" out of your own API server
    /// — and the mockup puts that badge next to the filename for exactly that reason.
    /// </summary>
    public static readonly StyledProperty<bool> SchemasFromClusterProperty =
        AvaloniaProperty.Register<WorkspaceView, bool>(nameof(SchemasFromCluster));

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

        // The page is built before a folder is picked, and again with a workspace already in hand when
        // you navigate back to it — so which of the two halves is on screen follows the DataContext
        // rather than being decided once in the constructor.
        DataContextChanged += (_, _) => ShowWorkspace(DataContext is WorkspaceViewModel);
        ShowWorkspace(DataContext is WorkspaceViewModel);
    }

    public bool SchemasFromCluster
    {
        get => GetValue(SchemasFromClusterProperty);
        set => SetValue(SchemasFromClusterProperty, value);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SchemasFromClusterProperty)
            SchemaSource.Text = SchemasFromCluster ? "schemas from cluster" : "bundled schemas";
    }

    private void ShowWorkspace(bool open)
    {
        Workspace.IsVisible = open;
        EmptyState.IsVisible = !open;
    }

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

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: TreeRow row } && DataContext is WorkspaceViewModel vm)
            vm.OpenNodeCommand.Execute(row.Node);
    }

    private void OnTabClick(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: OpenDocument document } && DataContext is WorkspaceViewModel vm)
            vm.ActiveDocument = document;
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: OpenDocument document } && DataContext is WorkspaceViewModel vm)
            vm.CloseTabCommand.Execute(document);
    }

    private void OnQuickFixClick(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: Problem { Fixes.Count: > 0 } problem })
            Editor.ApplyFix(problem.Fixes[0]);
    }
}
