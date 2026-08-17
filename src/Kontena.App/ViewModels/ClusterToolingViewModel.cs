using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Tooling;
using Kontena.Core.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>
/// One heading on the tools page: which tools go together, and the reason they do (KON-266).
/// </summary>
/// <param name="Title">The heading itself, e.g. "Working with clusters".</param>
/// <param name="Reason">Why these are grouped — and, for a group of tools you may not need, why
/// missing is a fine answer.</param>
/// <param name="Tools">The tools under it, in the order they are shown.</param>
public sealed record ToolGroup(string Title, string Reason, IReadOnlyList<ExternalTool> Tools)
{
    /// <summary>
    /// What Settings › Tools shows. Grouped by what you need the tool for rather than by who publishes
    /// it: kubectl and helm are needed for every cluster, including a remote one with nothing local
    /// about it, which is exactly why they no longer sit under Local clusters (KON-266).
    /// </summary>
    public static IReadOnlyList<ToolGroup> Default { get; } =
    [
        new("Working with clusters", "Needed for every cluster, local or remote.",
            [KnownTools.Kubectl, KnownTools.Helm, KnownTools.Kustomize]),

        new("Clusters on this machine", "Only if you build clusters here — missing is fine otherwise.",
            [KnownTools.Kind, KnownTools.Minikube]),

        // Podman is listed although Settings › Engines also talks about it: that page is about
        // connecting to an engine, this one is about whether the command is on the machine at all
        // (KON-255). Leaving it out is how its install hints for five package managers ended up
        // reaching nobody.
        new("Container engines", "kind and minikube can run their nodes on podman instead of Docker.",
            [KnownTools.Podman]),
    ];
}

/// <summary>The rows under one heading. Built once and patched in place, like the rows themselves.</summary>
public sealed class ToolGroupViewModel(string title, string reason)
{
    public string Title { get; } = title;
    public string Reason { get; } = reason;
    public ObservableCollection<ClusterToolRowViewModel> Tools { get; } = [];
}

/// <summary>
/// Settings › Tools — whether the external tools Kontena drives are on this machine, and how to get
/// them if not (KON-109, moved out of Local clusters by KON-266).
/// </summary>
/// <remarks>
/// Its own view model rather than another section on <see cref="SettingsViewModel"/>, which already
/// carries twelve optional constructor parameters and was three merge conflicts in one day. A new
/// screen is a new type.
/// </remarks>
public sealed partial class ClusterToolingViewModel : ViewModelBase, IDisposable
{
    private readonly ToolReadinessCheck _check;
    private readonly ToolInstaller _installer;
    private readonly ManagedToolStore _store;
    private readonly ToolUpdateCheck _updates;

    private CancellationTokenSource? _running;

    public ClusterToolingViewModel(
        IToolRunner? runner = null,
        IToolReleaseSource? releases = null,
        ManagedToolStore? store = null)
    {
        var toolRunner = runner ?? new ToolRunner();
        _store = store ?? new ManagedToolStore();
        _check = new ToolReadinessCheck(toolRunner, _store);
        _installer = new ToolInstaller(toolRunner, releases, _store);
        _updates = new ToolUpdateCheck(releases ?? new ToolReleaseSources(), _store);
    }

    /// <summary>
    /// The clock the update cache is aged against. Injectable so a test can move a day forward without
    /// waiting one — see <see cref="ToolUpdateCheck"/>.
    /// </summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Opens a documentation link in the browser; the shell owns that.</summary>
    public Action<string>? RequestOpenUrl { get; set; }

    /// <summary>Which tools this page shows, under which headings. A parameter so a test can narrow it.</summary>
    public IReadOnlyList<ToolGroup> Catalog { get; init; } = ToolGroup.Default;

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    /// <summary>Every row, headings ignored — what the update sweep walks.</summary>
    public IEnumerable<ClusterToolRowViewModel> Tools => Groups.SelectMany(g => g.Tools);

    /// <summary>Lines from whatever is running now — an install, or a download's progress.</summary>
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyTitle = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private bool _hasLoaded;

    /// <summary>Where Kontena keeps copies it fetched itself — shown so it is never a mystery.</summary>
    public string ManagedRoot => _store.Root;

    /// <summary>
    /// Re-check, as the button does: this is a fresh attempt, so a failure from the previous one stops
    /// being news.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        Error = null;
        await RefreshAsync();
    }

    /// <summary>
    /// Re-read the state without touching <see cref="Error"/>. Every run ends with one of these, and
    /// clearing the message here would wipe the explanation before it could be read — which is exactly
    /// what happened until a test caught it.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsChecking = true;

        try
        {
            foreach (var group in Catalog)
            {
                var readiness = await _check.CheckAllAsync(group.Tools);

                var rows = Groups.FirstOrDefault(g => g.Title == group.Title);
                if (rows is null)
                {
                    rows = new ToolGroupViewModel(group.Title, group.Reason);
                    Groups.Add(rows);
                }

                if (rows.Tools.Count == 0)
                {
                    foreach (var tool in readiness)
                        rows.Tools.Add(new ClusterToolRowViewModel(tool, this));
                }
                else
                {
                    // Patch in place so a row does not blink out and back while it is being read.
                    for (var i = 0; i < readiness.Count && i < rows.Tools.Count; i++)
                        rows.Tools[i].Update(readiness[i]);
                }
            }

            HasLoaded = true;
        }
        finally
        {
            IsChecking = false;
        }

        // Deliberately after the page is drawn and deliberately not awaited: this is the one thing here
        // that needs the network, and a page that waits for it would take as long as the slowest lookup
        // to show what is already known from disk (KON-153).
        _ = RefreshUpdatesAsync();
    }

    /// <summary>
    /// Fill in which tools have a newer release. Answers are cached for a day, so this is usually free;
    /// the first run of the day costs one lookup per tool. Offline it quietly finds nothing, which is
    /// the same as having nothing to say.
    /// </summary>
    public async Task RefreshUpdatesAsync(CancellationToken ct = default)
    {
        var now = Now();

        foreach (var row in Tools.ToList())
        {
            if (ct.IsCancellationRequested)
                return;

            row.SetUpdate(await _updates.CheckAsync(row.Tool, row.Version, now, ct));
        }
    }

    /// <summary>Run the machine's package manager, with its own output in view.</summary>
    public async Task InstallAsync(ClusterToolRowViewModel row, InstallHint hint)
    {
        await RunAsync($"Installing {row.Name}", async token =>
        {
            await foreach (var line in _installer.InstallWithPackageManagerAsync(hint, token))
                Output.Add(line.Text);
        });
    }

    /// <summary>
    /// Fetch the publisher's release into Kontena's own directory. The version and digest are named
    /// before anything is written, because this is the path where Kontena takes responsibility.
    /// </summary>
    public async Task DownloadAsync(ClusterToolRowViewModel row)
    {
        await RunAsync($"Downloading {row.Name}", async token =>
        {
            var download = await _installer.FindDownloadAsync(row.Tool, token);
            if (download is null)
            {
                Error = $"Could not look up a release for {row.Name}. " +
                        "Check the connection, or install it yourself — the documentation link is below.";
                return;
            }

            Output.Add($"{download.Version} from {download.Url}");
            Output.Add($"expecting sha256 {download.Sha256}");

            HasProgress = true;
            var progress = new Progress<long>(bytes => Progress = bytes / 1_000_000d);
            var path = await _installer.DownloadAsync(download, progress, token);

            Output.Add("checksum verified");
            Output.Add($"installed to {path}");
        });
    }

    /// <summary>
    /// Removing a copy Kontena installed is a delete, so it asks first (KON-126). Nothing else in the
    /// directory is touched, and the tool can be fetched again.
    /// </summary>
    public void ConfirmRemove(ClusterToolRowViewModel row)
    {
        Confirm(
            "Remove this copy",
            $"Remove Kontena's copy of {row.Name}? It goes from {_store.Root} and nothing else there " +
            "is touched. You can fetch it again, or install it with a package manager instead.",
            "Remove",
            async () =>
            {
                _store.Remove(row.Tool);
                await LoadAsync();
            });
    }

    /// <summary>
    /// Hand a tool to Kontena (KON-153): fetch a copy if there is not one yet, then mark it as the one
    /// that wins over whatever is on PATH.
    /// <para>
    /// Two steps rather than one button that only downloads, because a downloaded copy that never runs
    /// is the trap this whole thing exists to close — a system install wins by default, so fetching a
    /// newer kind without saying which one to use changes nothing at all.
    /// </para>
    /// </summary>
    public async Task PreferManagedAsync(ClusterToolRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_store.Record(row.Tool) is null)
        {
            await DownloadAsync(row);

            // The download reports its own failure. Preferring a copy that is not there would leave the
            // row claiming Kontena is in charge of something it does not have.
            if (_store.Record(row.Tool) is null)
                return;
        }

        _store.SetPreferred(row.Tool, true);
        await LoadAsync();
    }

    /// <summary>
    /// Give a tool back to the system install. The copy stays where it is — this is about which one
    /// runs, not about deleting anything; removing it is its own, confirmed act.
    /// </summary>
    public async Task PreferSystemAsync(ClusterToolRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        _store.SetPreferred(row.Tool, false);
        await LoadAsync();
    }

    public void OpenDocumentation(string url) => RequestOpenUrl?.Invoke(url);

    [RelayCommand]
    private void Cancel() => _running?.Cancel();

    /// <summary>
    /// Leaving the page mid-install stops it. A package manager left running against a page nobody is
    /// watching would finish out of sight and disagree with what the next check reports.
    /// </summary>
    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }

    private async Task RunAsync(string title, Func<CancellationToken, Task> work)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        BusyTitle = title;
        Error = null;
        Progress = 0;
        HasProgress = false;
        Output.Clear();

        _running = new CancellationTokenSource();

        try
        {
            await work(_running.Token);
        }
        catch (OperationCanceledException)
        {
            Output.Add("Cancelled.");
        }
        catch (ToolVerificationException ex)
        {
            // The one failure worth its own wording: this is not "the download failed", it is
            // "what arrived was not what was published".
            Error = ex.Message;
        }
        catch (ToolFailedException ex)
        {
            Error = ex.Complaint.Length > 0 ? ex.Complaint : ex.Message;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsBusy = false;
            HasProgress = false;
            await RefreshAsync();
        }
    }
}
