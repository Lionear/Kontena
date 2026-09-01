using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Kontena.Plugins.ManifestStudio.Git;
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

    /// <summary>
    /// Folders opened before, offered on the empty state (KON-434). Handed in rather than read here:
    /// the plugin owns the session and is the one that records them, and a view that reads its own
    /// history is a view that cannot be shown a different one in a test.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<RecentWorkspace>> RecentProperty =
        AvaloniaProperty.Register<WorkspaceView, IReadOnlyList<RecentWorkspace>>(nameof(Recent), []);

    /// <summary>
    /// Cloning a repository into a new workspace (KON-436). Handed in for the same reason
    /// <see cref="Recent"/> is: the view owns the folder picker and the page, the plugin owns the
    /// session. Null leaves the "Clone repository…" button off the card entirely — a button that
    /// cannot do anything is worse than one that is not there.
    /// </summary>
    public static readonly StyledProperty<CloneViewModel?> CloneProperty =
        AvaloniaProperty.Register<WorkspaceView, CloneViewModel?>(nameof(Clone));

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

    public IReadOnlyList<RecentWorkspace> Recent
    {
        get => GetValue(RecentProperty);
        set => SetValue(RecentProperty, value);
    }

    public CloneViewModel? Clone
    {
        get => GetValue(CloneProperty);
        set => SetValue(CloneProperty, value);
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

        if (change.Property == RecentProperty)
        {
            RecentList.ItemsSource = Recent;
            RecentPanel.IsVisible = Recent.Count > 0;
        }

        if (change.Property == CloneProperty)
        {
            if (change.OldValue is CloneViewModel previous)
                previous.Cloned -= OnCloned;

            ClonePanel.DataContext = Clone;
            CloneButton.IsVisible = Clone is not null;

            if (Clone is { } clone)
                clone.Cloned += OnCloned;
        }
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
        if (await PickFolderAsync() is { } path)
            Open(path);
    }

    /// <summary>Unfolds the clone form on the same card. Kept a toggle rather than a one-way reveal so
    /// the page can be put back the way it was without leaving and coming back.</summary>
    private void OnCloneRepositoryClick(object? sender, RoutedEventArgs e) =>
        ClonePanel.IsVisible = !ClonePanel.IsVisible;

    /// <summary>Picks the folder to clone into — the same picker as "Open folder…", because it is the
    /// same question asked a moment earlier.</summary>
    private async void OnChooseCloneFolderClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path && Clone is { } clone)
            clone.ParentFolder = path;
    }

    /// <summary>The clone landed; from here on it is a folder like any other, so it goes in through the
    /// one <see cref="Open"/> the picker and the recent list also use.</summary>
    private void OnCloned(object? sender, string path) => Open(path);

    /// <summary>
    /// The folder picker, the one piece of this view no test can drive: it needs a real
    /// <see cref="TopLevel"/> storage provider. Null means the user cancelled, or picked something with
    /// no local path (a cloud location) — both of which are "carry on", not an error.
    /// </summary>
    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <summary>
    /// Reopens a folder from the recent list (KON-434). The same path as the picker, deliberately: the
    /// plugin learns about a workspace through one event whichever way it was chosen, so git, the plan
    /// page and the recent list itself cannot end up describing different folders.
    /// </summary>
    private void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: RecentWorkspace entry })
            return;

        try
        {
            Open(entry.RootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The list leaves out folders that were gone when it was read, so getting here means it
            // went away while this page was open. A row that silently does nothing is a bug report, so
            // it leaves the list instead. Only the list — the file keeps it, for the same reason
            // RecentWorkspaceStore.Read does not forget an unmounted volume.
            Recent = [.. Recent.Where(other => !ReferenceEquals(other, entry))];
        }
    }

    private void Open(string path)
    {
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
