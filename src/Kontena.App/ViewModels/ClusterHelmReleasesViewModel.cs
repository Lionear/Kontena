using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Rendering;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The Helm releases installed in the cluster (KON-473). Present whether or not helm is installed, like
/// Alerts (KON-207): without it the page says what is missing rather than the entry disappearing.
/// <para>
/// Watches Secrets, because that is where helm writes every revision — an upgrade or uninstall made from
/// a terminal shows up here without a refresh.
/// </para>
/// </summary>
public partial class ClusterHelmReleasesViewModel : ClusterListPageViewModel<HelmReleaseRow>
{
    private readonly IHelmReleases? _helm;
    private readonly string? _namespace;
    private readonly Action<HelmRelease>? _onOpenDetail;
    private readonly Action<HelmRelease>? _onUninstall;

    /// <param name="onUninstall">The shell's confirm-then-uninstall, shared with the detail page so the
    /// two cannot word or route it differently.</param>
    public ClusterHelmReleasesViewModel(
        IClusterEngine cluster, string? @namespace,
        Action<HelmRelease>? onOpenDetail = null, Action<HelmRelease>? onUninstall = null)
        : base(cluster, HelmOf(cluster) is null ? null : GroupVersionKind.Secret, @namespace)
    {
        _helm = HelmOf(cluster);
        _namespace = @namespace;
        _onOpenDetail = onOpenDetail;
        _onUninstall = onUninstall;
        _ = LoadAsync();
        StartWatching();
    }

    /// <summary>The cluster's releases, or null when it has no helm to ask.</summary>
    internal static IHelmReleases? HelmOf(IClusterEngine cluster) =>
        cluster.Capabilities.Helm && cluster is IHelmAware aware ? aware.Helm : null;

    public bool IsHelmMissing => _helm is null;

    /// <summary>What helm said when the list could not be read — unreachable cluster, RBAC, ….</summary>
    [ObservableProperty] private string? _error;

    public bool ShowEmpty => IsEmpty && Error is null && !IsHelmMissing;

    public override string SearchPlaceholder => "Search releases…";

    protected override async Task<IReadOnlyList<HelmReleaseRow>> LoadRowsAsync(CancellationToken ct)
    {
        if (_helm is null)
            return [];

        try
        {
            var releases = await _helm.ListAsync(_namespace, ct);
            Error = null;
            return [.. releases.Select(r => new HelmReleaseRow(r, _onOpenDetail, _onUninstall))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            return [];
        }
    }

    protected override void OnFiltered() => OnPropertyChanged(nameof(ShowEmpty));

    protected override bool Matches(HelmReleaseRow row, string term) =>
        Contains(row.Name, term) || Contains(row.Namespace, term) || Contains(row.Chart, term) || Contains(row.Status, term);

    protected override IReadOnlyDictionary<string, Func<HelmReleaseRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<HelmReleaseRow, IComparable>>(StringComparer.Ordinal)
        {
            ["NAME"] = r => r.Name,
            ["NAMESPACE"] = r => r.Namespace,
            ["CHART"] = r => r.Chart,
            ["STATUS"] = r => r.Status,
            ["REVISION"] = r => r.Revision,
            ["UPDATED"] = r => r.UpdatedAt,
        };
}

public sealed partial class HelmReleaseRow
{
    private readonly Action<HelmRelease>? _onOpenDetail;
    private readonly Action<HelmRelease>? _onUninstall;

    public HelmReleaseRow(HelmRelease release, Action<HelmRelease>? onOpenDetail = null, Action<HelmRelease>? onUninstall = null)
    {
        ArgumentNullException.ThrowIfNull(release);

        Release = release;
        _onOpenDetail = onOpenDetail;
        _onUninstall = onUninstall;
        CanOpen = onOpenDetail is not null;
        CanUninstall = onUninstall is not null;
    }

    public HelmRelease Release { get; }

    public string Name => Release.Name;
    public string Namespace => Release.Namespace;
    public string Chart => HelmWording.Chart(Release);
    public string AppVersion => Release.AppVersion.Length > 0 ? Release.AppVersion : "—";
    public string Status => Release.Status;
    public bool IsHealthy => HelmWording.IsHealthy(Release.Status);
    public int Revision => Release.Revision;
    public DateTimeOffset UpdatedAt => Release.Updated ?? DateTimeOffset.MinValue;
    public string Updated => HelmWording.Ago(Release.Updated);

    public bool CanOpen { get; }
    public bool CanUninstall { get; }

    [RelayCommand]
    private void Open() => _onOpenDetail?.Invoke(Release);

    [RelayCommand]
    private void Uninstall() => _onUninstall?.Invoke(Release);
}

/// <summary>
/// One release in full (KON-473): its values, what it rendered, its history — and the three things that
/// change it. Upgrade shows the values diff before it may run, the same review-then-act order the apply
/// page's dry-run has (KON-86); rollback and uninstall are always confirmed.
/// </summary>
public sealed partial class ClusterHelmReleaseDetailViewModel : ViewModelBase
{
    private readonly IHelmReleases _helm;
    private readonly Action<HelmRelease>? _onUninstall;

    /// <summary>The user-supplied values as they are now; what the upgrade diff is taken against.</summary>
    private string _currentValues = string.Empty;

    public ClusterHelmReleaseDetailViewModel(IHelmReleases helm, HelmRelease release, Action<HelmRelease>? onUninstall = null)
    {
        _helm = helm;
        _release = release;
        _onUninstall = onUninstall;
        _upgradeChart = release.Chart;
        _upgradeVersion = release.ChartVersion;
        _ = LoadAsync();
        _ = GuessChartAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chart), nameof(AppVersion), nameof(Status), nameof(IsHealthy), nameof(Revision), nameof(Updated))]
    private HelmRelease _release;

    public string Name => Release.Name;
    public string Namespace => Release.Namespace;
    public string Chart => HelmWording.Chart(Release);
    public string AppVersion => Release.AppVersion.Length > 0 ? Release.AppVersion : "—";
    public string Status => Release.Status;
    public bool IsHealthy => HelmWording.IsHealthy(Release.Status);
    public int Revision => Release.Revision;
    public string Updated => HelmWording.Ago(Release.Updated);

    public bool CanUninstall => _onUninstall is not null;

    [ObservableProperty] private string? _error;

    // ── Tabs ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValuesSelected), nameof(IsManifestSelected), nameof(IsHistorySelected), nameof(IsUpgradeSelected))]
    private string _tab = "values";

    public bool IsValuesSelected => Tab == "values";
    public bool IsManifestSelected => Tab == "manifest";
    public bool IsHistorySelected => Tab == "history";
    public bool IsUpgradeSelected => Tab == "upgrade";

    [RelayCommand]
    private void SelectTab(string tab) => Tab = tab;

    // ── Values, manifest, history ───────────────────────────────────────────

    /// <summary>Off: what the user supplied. On: merged with the chart's defaults — what the templates saw.</summary>
    [ObservableProperty] private bool _showAllValues;

    [ObservableProperty] private string _valuesText = string.Empty;
    [ObservableProperty] private string _manifestText = string.Empty;

    public ObservableCollection<HelmRevisionRow> History { get; } = [];

    partial void OnShowAllValuesChanged(bool value) => _ = Guard(LoadValuesAsync);

    public Task LoadAsync() => Guard(async () =>
    {
        // The header too: after a rollback the revision, status and chart version have all moved.
        var releases = await _helm.ListAsync(Release.Namespace);
        if (releases.FirstOrDefault(r => r.Name == Release.Name) is { } fresh)
            Release = fresh;

        await LoadValuesAsync();
        ManifestText = await _helm.GetManifestAsync(Release.Name, Release.Namespace);

        var history = await _helm.GetHistoryAsync(Release.Name, Release.Namespace);
        History.Clear();
        foreach (var revision in history.OrderByDescending(h => h.Revision))
            History.Add(new HelmRevisionRow(revision, Release.Revision, ConfirmRollback));
    });

    private async Task LoadValuesAsync()
    {
        _currentValues = await _helm.GetValuesAsync(Release.Name, Release.Namespace);
        ValuesText = ShowAllValues
            ? await _helm.GetValuesAsync(Release.Name, Release.Namespace, all: true)
            : _currentValues;

        // The editor starts from what is there now, and only until someone has typed in it.
        if (!IsUpgradeEdited)
            SetUpgradeValues(_currentValues);
    }

    /// <summary>Reads fail into <see cref="Error"/>: a page that throws shows nothing at all.</summary>
    private async Task Guard(Func<Task> read)
    {
        try
        {
            Error = null;
            await read();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    // ── Rollback ────────────────────────────────────────────────────────────

    internal void ConfirmRollback(HelmRevision revision) =>
        Confirm(
            $"Roll {Release.Name} back to revision {revision.Revision}?",
            $"Helm re-applies the chart and values of revision {revision.Revision} ({revision.Chart}) as a new revision. " +
            "Whatever the current revision changed is undone in the cluster.",
            "Roll back",
            async () =>
            {
                if (await _helm.RollbackAsync(Release.Name, Release.Namespace, revision.Revision) is { } error)
                    throw new InvalidOperationException(error);

                IsUpgradeEdited = false;
                await LoadAsync();
            });

    [RelayCommand]
    private void Uninstall() => _onUninstall?.Invoke(Release);

    // ── Upgrade ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Helm does not record where a chart came from, so this starts as the bare chart name and becomes
    /// <c>repo/chart</c> when exactly one configured repository offers a chart by that name.
    /// </summary>
    [ObservableProperty] private string _upgradeChart;

    [ObservableProperty] private string _upgradeVersion;
    [ObservableProperty] private string _upgradeValues = string.Empty;

    /// <summary>The values diff the upgrade will apply; empty until reviewed, and again after any edit.</summary>
    public ObservableCollection<DiffLineRow> UpgradeDiff { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpgrade))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private bool _isReviewed;

    [ObservableProperty] private string? _reviewNote;

    private bool IsUpgradeEdited { get; set; }
    private bool _settingValues;

    public bool HasUpgradeDiff => UpgradeDiff.Count > 0;

    public bool CanUpgrade => IsReviewed && UpgradeChart.Trim().Length > 0;

    partial void OnUpgradeChartChanged(string value) => Unreview();
    partial void OnUpgradeVersionChanged(string value) => Unreview();

    partial void OnUpgradeValuesChanged(string value)
    {
        if (!_settingValues)
            IsUpgradeEdited = true;
        Unreview();
    }

    private void SetUpgradeValues(string values)
    {
        _settingValues = true;
        UpgradeValues = values;
        _settingValues = false;
    }

    private void Unreview()
    {
        IsReviewed = false;
        ReviewNote = null;
        UpgradeDiff.Clear();
        OnPropertyChanged(nameof(CanUpgrade));
        OnPropertyChanged(nameof(HasUpgradeDiff));
    }

    [RelayCommand]
    private void Review()
    {
        Unreview();

        var diff = ManifestDiff.Compute(_currentValues, UpgradeValues);
        foreach (var line in diff.Split('\n').Where(l => l.Length > 0))
            UpgradeDiff.Add(new DiffLineRow(line));

        var version = UpgradeVersion.Trim().Length > 0 ? UpgradeVersion.Trim() : "the newest version";
        ReviewNote = UpgradeDiff.Count == 0
            ? $"The values do not change. The upgrade takes {Release.Name} to {UpgradeChart.Trim()} {version}."
            : $"Upgrading takes {Release.Name} to {UpgradeChart.Trim()} {version} with these values changes.";

        OnPropertyChanged(nameof(HasUpgradeDiff));
        IsReviewed = true;
    }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private void Upgrade() =>
        Confirm(
            $"Upgrade {Release.Name}?",
            ReviewNote ?? string.Empty,
            "Upgrade",
            async () =>
            {
                var error = await _helm.UpgradeAsync(new HelmUpgrade
                {
                    Release = Release.Name,
                    Namespace = Release.Namespace,
                    Chart = UpgradeChart.Trim(),
                    Version = UpgradeVersion.Trim(),
                    ValuesYaml = UpgradeValues,
                });
                if (error is not null)
                    throw new InvalidOperationException(error);

                IsUpgradeEdited = false;
                await LoadAsync();
                Unreview();
            },
            destructive: false);

    private async Task GuessChartAsync()
    {
        if (!HelmRepos.IsAvailable)
            return;

        var matches = (await HelmRepos.SearchAsync(Release.Chart))
            .Where(c => c.ShortName == Release.Chart)
            .ToList();

        // Only an unambiguous match, and only while the field still holds the bare name.
        if (matches is [var only] && UpgradeChart == Release.Chart)
            UpgradeChart = only.Name;
    }
}

public sealed partial class HelmRevisionRow
{
    private readonly Action<HelmRevision> _onRollback;

    public HelmRevisionRow(HelmRevision revision, int current, Action<HelmRevision> onRollback)
    {
        Revision = revision;
        _onRollback = onRollback;
        IsCurrent = revision.Revision == current;
    }

    public HelmRevision Revision { get; }

    public int Number => Revision.Revision;
    public string Status => Revision.Status;
    public bool IsHealthy => HelmWording.IsHealthy(Revision.Status);
    public string Chart => Revision.Chart;
    public string Description => Revision.Description;
    public string Updated => HelmWording.Ago(Revision.Updated);

    /// <summary>Rolling back to the revision you are on is a new revision that changes nothing.</summary>
    public bool IsCurrent { get; }
    public bool CanRollback => !IsCurrent;

    [RelayCommand]
    private void Rollback() => _onRollback(Revision);
}

/// <summary>Words shared by the list, the detail and the shell's uninstall confirm.</summary>
internal static class HelmWording
{
    public static string Chart(HelmRelease release) =>
        release.ChartVersion.Length > 0 ? $"{release.Chart} {release.ChartVersion}" : release.Chart;

    /// <summary>Superseded is healthy too: it is simply not the current revision.</summary>
    public static bool IsHealthy(string status) => status is "deployed" or "superseded";

    public static string Ago(DateTimeOffset? when) =>
        when is { } t ? Format.Duration(DateTimeOffset.UtcNow - t) : "—";

    public static (string Title, string Message) Uninstall(HelmRelease release) =>
        ($"Uninstall {release.Name}?",
         $"Every resource the release created in {release.Namespace} is deleted, and helm forgets its history — " +
         "there is no rollback from an uninstall. Resources annotated helm.sh/resource-policy: keep, and volume " +
         "claims a StatefulSet created, are left behind.");
}
