using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Rendering;

namespace Kontena.App.ViewModels;

/// <summary>Where the manifest on the page came from.</summary>
public enum ManifestSourceKind
{
    /// <summary>Typed, pasted, or loaded from a file — the manifest is the source.</summary>
    Paste,

    /// <summary>Built from a kustomization directory (KON-88).</summary>
    Kustomize,

    /// <summary>Rendered from a Helm chart and its values (KON-89).</summary>
    Helm,
}

/// <summary>
/// The render sources in front of the apply page (KON-88, KON-89). A kustomization or a chart is
/// turned into flat YAML here; from that point on it is an ordinary bundle and takes exactly the
/// route a pasted one does — dry-run, plan, diff, apply. Rendering is a step, not a second flow.
/// </summary>
public partial class ApplyManifestViewModel
{
    private readonly KustomizeRenderer _kustomize = new();
    private readonly HelmRenderer _helm = new();

    [ObservableProperty] private ManifestSourceKind _sourceKind = ManifestSourceKind.Paste;

    public bool IsPasteSource => SourceKind == ManifestSourceKind.Paste;
    public bool IsKustomizeSource => SourceKind == ManifestSourceKind.Kustomize;
    public bool IsHelmSource => SourceKind == ManifestSourceKind.Helm;
    public bool IsRenderedSource => !IsPasteSource;

    /// <summary>The command the last render ran, shown so it can be reproduced in a terminal.</summary>
    [ObservableProperty] private string _lastCommand = string.Empty;

    [ObservableProperty] private bool _isRendering;

    /// <summary>Findings from the last render: build errors, lint notes, the resource count.</summary>
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    // ── Kustomize ────────────────────────────────────────────────────────────

    /// <summary>The overlay directory — <c>overlays/prod</c>, not the repository root.</summary>
    [ObservableProperty] private string _kustomizePath = string.Empty;

    /// <summary>Whether a kustomize (or kubectl) build is possible on this machine at all.</summary>
    public bool IsKustomizeInstalled => _kustomize.Locate() is not null;

    // ── Helm ─────────────────────────────────────────────────────────────────

    /// <summary>A chart directory, a packaged chart, or <c>repo/name</c>.</summary>
    [ObservableProperty] private string _chart = string.Empty;

    /// <summary>What templates see as <c>.Release.Name</c>; it ends up in most resource names.</summary>
    [ObservableProperty] private string _releaseName = string.Empty;

    /// <summary>Chart version; empty takes whatever the repository offers as newest.</summary>
    [ObservableProperty] private string _chartVersion = string.Empty;

    /// <summary>
    /// Where documents that name no namespace of their own end up — the Helm render's
    /// <c>--namespace</c> and the bundle's fallback are the same choice. Defaults to the namespace
    /// picker's selection; empty leaves it to the kube-context.
    /// </summary>
    [ObservableProperty] private string _renderNamespace = string.Empty;

    /// <summary>"namespace app" or "the context's namespace" — what the action bar states.</summary>
    public string NamespaceLabel => RenderNamespace.Trim() is { Length: > 0 } ns
        ? $"namespace {ns}"
        : "the context's namespace";

    partial void OnRenderNamespaceChanged(string value) => OnPropertyChanged(nameof(NamespaceLabel));

    /// <summary>One <c>key=value</c> per line — applied after the values files, as helm does.</summary>
    [ObservableProperty] private string _setValues = string.Empty;

    [ObservableProperty] private bool _includeCrds = true;
    [ObservableProperty] private bool _runLint = true;

    /// <summary>Values files in precedence order; later files win.</summary>
    public ObservableCollection<string> ValuesFiles { get; } = [];

    public bool HasValuesFiles => ValuesFiles.Count > 0;

    public bool IsHelmInstalled => _helm.Locate() is not null;

    // ── Chart repositories ───────────────────────────────────────────────────

    /// <summary>Helm's own repository list — the same repos the user's terminal sees.</summary>
    public ObservableCollection<HelmRepo> Repos { get; } = [];

    /// <summary>Charts matching the current search, ready to fill the chart field.</summary>
    public ObservableCollection<HelmChart> Charts { get; } = [];

    [ObservableProperty] private string _chartSearch = string.Empty;
    [ObservableProperty] private string _newRepoName = string.Empty;
    [ObservableProperty] private string _newRepoUrl = string.Empty;
    [ObservableProperty] private bool _isBrowsingCharts;
    [ObservableProperty] private string? _repoStatus;

    public bool HasRepos => Repos.Count > 0;

    /// <summary>Switch source. Takes the name rather than the value so the view stays declarative.</summary>
    [RelayCommand]
    private void SelectSource(string? kind) =>
        SourceKind = Enum.TryParse<ManifestSourceKind>(kind, out var parsed) ? parsed : ManifestSourceKind.Paste;

    /// <summary>Fill the chart fields from a search hit; the qualified name is what helm wants.</summary>
    [RelayCommand]
    private void UseChart(HelmChart? chart)
    {
        if (chart is null)
            return;

        Chart = chart.Name;
        ChartVersion = chart.Version;

        if (ReleaseName.Length == 0)
            ReleaseName = chart.ShortName;
    }

    [RelayCommand]
    private async Task LoadReposAsync()
    {
        Repos.Clear();
        foreach (var repo in await HelmRepos.ListAsync())
            Repos.Add(repo);

        OnPropertyChanged(nameof(HasRepos));

        // An empty search lists everything the repositories offer — a browsable starting point.
        await SearchChartsAsync();
    }

    [RelayCommand]
    private async Task SearchChartsAsync()
    {
        IsBrowsingCharts = true;
        try
        {
            Charts.Clear();
            foreach (var chart in await HelmRepos.SearchAsync(ChartSearch))
                Charts.Add(chart);

            RepoStatus = Charts.Count == 0 && Repos.Count > 0
                ? "No charts matched. Try 'Update repos' — the local index may be stale."
                : null;
        }
        finally
        {
            IsBrowsingCharts = false;
        }
    }

    [RelayCommand]
    private async Task UpdateReposAsync()
    {
        IsBrowsingCharts = true;
        try
        {
            RepoStatus = await HelmRepos.UpdateAsync();
        }
        finally
        {
            IsBrowsingCharts = false;
        }

        await SearchChartsAsync();
    }

    [RelayCommand]
    private async Task AddRepoAsync()
    {
        RepoStatus = await HelmRepos.AddAsync(NewRepoName, NewRepoUrl);
        if (RepoStatus is not null)
            return;

        NewRepoName = string.Empty;
        NewRepoUrl = string.Empty;
        await LoadReposAsync();
    }

    [RelayCommand]
    private async Task RemoveRepoAsync(HelmRepo? repo)
    {
        if (repo is null)
            return;

        RepoStatus = await HelmRepos.RemoveAsync(repo.Name);
        if (RepoStatus is null)
            await LoadReposAsync();
    }

    // ── Values files ─────────────────────────────────────────────────────────

    /// <summary>Called by the view once the file picker has produced a path.</summary>
    public void AddValuesFile(string path)
    {
        if (path.Length == 0 || ValuesFiles.Contains(path, StringComparer.Ordinal))
            return;

        ValuesFiles.Add(path);
        OnPropertyChanged(nameof(HasValuesFiles));
    }

    [RelayCommand]
    private void RemoveValuesFile(string? path)
    {
        if (path is not null && ValuesFiles.Remove(path))
            OnPropertyChanged(nameof(HasValuesFiles));
    }

    /// <summary>Called by the view once the folder picker has produced a path.</summary>
    public void SetKustomizePath(string path) => KustomizePath = path;

    /// <summary>Called by the view once the chart folder picker has produced a path.</summary>
    public void SetChartPath(string path)
    {
        Chart = path;
        ChartVersion = string.Empty;

        if (ReleaseName.Length == 0)
            ReleaseName = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RenderAsync()
    {
        IsRendering = true;
        Error = null;
        Diagnostics.Clear();
        OnPropertyChanged(nameof(HasDiagnostics));

        try
        {
            var result = SourceKind switch
            {
                ManifestSourceKind.Kustomize => await _kustomize.RenderAsync(new KustomizeRequest
                {
                    Path = KustomizePath,
                }),
                ManifestSourceKind.Helm => await _helm.RenderAsync(new HelmRequest
                {
                    Chart = Chart,
                    ReleaseName = ReleaseName,
                    Version = ChartVersion,
                    Namespace = RenderNamespace,
                    ValuesFiles = [.. ValuesFiles],
                    Sets = SplitSets(SetValues),
                    IncludeCrds = IncludeCrds,
                    Lint = RunLint,
                }),
                _ => null,
            };

            if (result is null)
                return;

            LastCommand = result.Command;

            foreach (var diagnostic in result.Diagnostics)
                Diagnostics.Add(new DiagnosticRow(diagnostic));

            OnPropertyChanged(nameof(HasDiagnostics));

            if (!result.Ok)
            {
                // Keep whatever was on the page: replacing it with nothing would lose the
                // previous render while the user is fixing the input.
                Error = $"{(SourceKind == ManifestSourceKind.Helm ? "Helm" : "Kustomize")} produced no usable manifests.";
                return;
            }

            YamlText = result.Yaml;
            Source = Describe(result);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRendering = false;
        }
    }

    /// <summary>"overlays/prod · 14 resources" — what the editor is now showing.</summary>
    private string Describe(RenderResult result)
    {
        var count = result.DocumentCount.ToString(CultureInfo.InvariantCulture);
        var what = result.DocumentCount == 1 ? "resource" : "resources";

        var label = SourceKind switch
        {
            ManifestSourceKind.Kustomize => Leaf(KustomizePath),
            ManifestSourceKind.Helm => $"{Leaf(Chart)} · {ReleaseName}",
            _ => "pasted",
        };

        return $"{label} · {count} {what}";
    }

    /// <summary>
    /// The last segment of a path, so the header reads "guestbook" rather than the whole tree.
    /// A <c>repo/chart</c> reference is already short and keeps both halves.
    /// </summary>
    private static string Leaf(string source)
    {
        var trimmed = source.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return System.IO.Directory.Exists(trimmed) || System.IO.File.Exists(trimmed)
            ? System.IO.Path.GetFileName(trimmed)
            : trimmed;
    }

    /// <summary>One override per line, blank lines ignored.</summary>
    private static IReadOnlyList<string> SplitSets(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    partial void OnSourceKindChanged(ManifestSourceKind value)
    {
        OnPropertyChanged(nameof(IsPasteSource));
        OnPropertyChanged(nameof(IsKustomizeSource));
        OnPropertyChanged(nameof(IsHelmSource));
        OnPropertyChanged(nameof(IsRenderedSource));

        Diagnostics.Clear();
        OnPropertyChanged(nameof(HasDiagnostics));
        Error = null;

        // The repository list is helm's, and reading it costs a process — only when it is asked for.
        if (value == ManifestSourceKind.Helm && Repos.Count == 0 && IsHelmInstalled)
            _ = LoadReposAsync();
    }
}

/// <summary>One render finding, coloured by severity.</summary>
public sealed class DiagnosticRow
{
    public DiagnosticRow(RenderDiagnostic diagnostic)
    {
        Message = diagnostic.Message;
        Source = diagnostic.Source;

        (Glyph, var colour) = diagnostic.Severity switch
        {
            RenderSeverity.Error => ("!", "#F87171"),
            RenderSeverity.Warning => ("▲", "#F5B14C"),
            _ => ("i", "#8A94A2"),
        };

        Accent = new SolidColorBrush(Color.Parse(colour));
    }

    public string Glyph { get; }
    public string Message { get; }
    public string Source { get; }
    public IBrush Accent { get; }
    public bool HasSource => Source.Length > 0;
}
