using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Kontena.Plugins.ManifestStudio.Apply;
using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk;
using Kontena.Sdk.Orchestration;

namespace Kontena.Plugins.ManifestStudio;

/// <summary>
/// The entry point (KON-296): what the host loads, and the three pages it contributes — Editor, Plan
/// and Source control.
/// <para>
/// It is also the only thing that holds the session together. The pages are separate controls, built
/// fresh each time one is opened, but they are one workspace: the folder you picked in the editor is
/// what Plan applies and what Source control commits. That state lives here because this instance is
/// what the loader keeps.
/// </para>
/// </summary>
public sealed class ManifestStudioPlugin : IUiPlugin
{
    private WorkspaceViewModel? _workspace;

    // One git model for the whole session, not one per page: the Source control page and the editor's
    // file badges are two views of the same `git status`, and two models would let them disagree about
    // what changed (KON-427).
    private GitViewModel? _git;

    // Kept across navigations, because a fresh SchemaIndex would refetch every OpenAPI document the
    // first time you type in each page. Rebuilt when the cluster changes — schemas are that cluster's.
    private SchemaIndex? _schemas;
    private IClusterEngine? _schemaSource;

    public EngineManifest Manifest => new()
    {
        Id = "com.kontena.manifeststudio",
        Name = "Manifest Studio",
        Version = "0.1.0",
        Author = "Kontena",
        Description = "Write, validate and apply Kubernetes manifests from a folder or Git repository.",
        MinSdkVersion = "0.4.0",
        ContributesUi = true,
    };

    public IEnumerable<PluginPage> GetPages() =>
    [
        new PluginPage("editor", "Editor", "IconLayers", CreateEditor),
        new PluginPage("plan", "Plan & apply", "IconPlay", CreatePlan),
        new PluginPage("source", "Source control", "IconTag", CreateSourceControl),
    ];

    private WorkspaceView CreateEditor(IPluginHost host)
    {
        var view = new WorkspaceView
        {
            Schemas = SchemasFor(host),
            SchemasFromCluster = host.Cluster is not null,
        };

        if (_workspace is not null)
        {
            view.DataContext = _workspace;

            // The folder is on disk and other things write to it, so what git said last time this page
            // was open is history, not state.
            _git?.RefreshCommand.Execute(null);
        }

        view.WorkspaceOpened += (_, workspace) =>
        {
            _workspace = workspace;
            AttachGit(workspace);
        };

        return view;
    }

    /// <summary>Points a fresh git model at the new workspace and keeps the file pane's badges following
    /// it. A folder that is not a repository simply reports an error and leaves the badges off, which is
    /// the state the tree already renders.</summary>
    private void AttachGit(WorkspaceViewModel workspace)
    {
        var git = new GitViewModel(new GitCli(), workspace.Workspace.RootPath);
        git.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitViewModel.Status))
                workspace.SetGitStatus(git.Status);
        };

        _git = git;
        git.RefreshCommand.Execute(null);
    }

    private Control CreatePlan(IPluginHost host)
    {
        if (_workspace is not { } workspace)
            return Explain("Open a folder in the Editor first — a plan is a plan of what is in it.");

        if (host.Cluster is not { } cluster)
            return Explain(
                "Plan and apply need a cluster. Open one from the backend switcher and come back — "
                + "the dry-run and the diff come from the API server, not from here.");

        var view = new PlanApplyView { DataContext = new PlanApplyViewModel(new ClusterEngineApplyTarget(cluster)) };

        // Bound rather than read once: switching tabs in the editor is how you choose what to plan, and
        // this page has no tab strip of its own to say it again.
        view[!PlanApplyView.DocumentProperty] =
            new Binding(nameof(WorkspaceViewModel.ActiveDocument)) { Source = workspace };

        return view;
    }

    private Control CreateSourceControl(IPluginHost host)
    {
        if (_workspace is not { } workspace)
            return Explain("Open a folder in the Editor first — source control follows the workspace.");

        _git ??= new GitViewModel(new GitCli(), workspace.Workspace.RootPath);
        return new GitView { DataContext = _git };
    }

    private SchemaIndex SchemasFor(IPluginHost host)
    {
        if (_schemas is not null && ReferenceEquals(host.Cluster, _schemaSource))
            return _schemas;

        _schemaSource = host.Cluster;

        // Without a cluster the bundled set still knows Deployment and Service, but not this cluster's
        // custom resources — Plan §3. Unknown is not wrong: the editor stays quiet about what it cannot
        // verify rather than underlining it.
        return _schemas = new SchemaIndex(host.Cluster is { } cluster
            ? new ClusterEngineSchemaSource(cluster)
            : new BundledSchemaSource());
    }

    /// <summary>A page that says why it has nothing to show. A blank panel is a bug report.</summary>
    private static TextBlock Explain(string message) => new()
    {
        Margin = new Thickness(24),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 520,
        Text = message,
    };
}
