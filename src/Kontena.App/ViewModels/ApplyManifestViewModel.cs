using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// The declarative heart (KON-69): paste or load a manifest bundle, run a server-side dry-run to
/// see the plan and the unified diff, then apply it for real. Dry-run first is the point — nothing
/// reaches the cluster until the plan has been seen.
/// </summary>
public partial class ApplyManifestViewModel : ViewModelBase
{
    private readonly IClusterEngine _cluster;
    private readonly Func<Task>? _onApplied;

    /// <param name="onApplied">Invoked after a real apply so the shell can refresh the grids and
    /// nav counts. Passed by constructor, not init-property, so it is set before any command runs.</param>
    /// <param name="ns">The namespace picker's selection, used as the default for a Helm render.</param>
    public ApplyManifestViewModel(
        IClusterEngine cluster, string? context, Func<Task>? onApplied = null, string? ns = null)
    {
        _cluster = cluster;
        _onApplied = onApplied;
        ContextName = context ?? cluster.Contexts.FirstOrDefault(c => c.IsCurrent)?.Name ?? "cluster";
        _yamlText = SampleManifest;
        _renderNamespace = ns ?? string.Empty;
    }

    /// <summary>A worked example, so the page is useful the moment it opens.</summary>
    private const string SampleManifest = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: payments-worker
          namespace: app
        spec:
          replicas: 2
          selector:
            matchLabels: {app: payments-worker}
          template:
            spec:
              containers:
                - name: worker
                  image: payments/worker:1.8.4
        """;

    public string ContextName { get; }

    [ObservableProperty] private string _yamlText;

    [ObservableProperty] private string _source = "pasted";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    /// <summary>
    /// What the run is doing right now, while it is doing it (KON-381). The plan only appears once
    /// every document has an outcome, and a chart's worth of resources — plus up to thirty seconds
    /// waiting for its CRDs to be served — is a long time for a page to look like it has hung.
    /// </summary>
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Set once a dry-run or apply has produced a plan.</summary>
    [ObservableProperty] private bool _hasPlan;

    /// <summary>True while the plan is a preview; false once it reflects a real apply.</summary>
    [ObservableProperty] private bool _isPreview = true;

    /// <summary>The plan, one row per document in the bundle.</summary>
    public ObservableCollection<ApplyPlanRow> Plan { get; } = [];

    /// <summary>The rows currently shown — <see cref="Plan"/> minus the buckets switched off.</summary>
    public ObservableCollection<ApplyPlanRow> VisiblePlan { get; } = [];

    /// <summary>
    /// The rollup, and the filter: one chip per outcome, each switching its rows on and off. A
    /// chart renders dozens of resources of which a handful actually change, and an undifferentiated
    /// list of them is not a plan anyone can act on.
    /// </summary>
    public ObservableCollection<PlanBucket> Buckets { get; } = [];

    public bool HasBuckets => Buckets.Count > 0;

    /// <summary>Above this, a plan stops being readable at a glance and the no-ops start hidden.</summary>
    private const int BigPlan = 10;

    /// <summary>"5 unchanged hidden" — so a filtered plan never looks like the whole plan.</summary>
    public string HiddenNote
    {
        get
        {
            var hidden = Plan.Count - VisiblePlan.Count;
            return hidden > 0
                ? $"{hidden.ToString(CultureInfo.InvariantCulture)} hidden"
                : string.Empty;
        }
    }

    public bool HasHidden => Plan.Count > VisiblePlan.Count;

    [RelayCommand]
    private void ToggleBucket(PlanBucket? bucket)
    {
        if (bucket is null)
            return;

        bucket.IsOn = !bucket.IsOn;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var on = Buckets.Where(b => b.IsOn).Select(b => b.Kind).ToHashSet();

        VisiblePlan.Clear();

        // What needs attention first. A chart renders dozens of resources in template order, which
        // buries the four that actually change; the plan is a verdict, not the document.
        foreach (var row in Plan.Where(r => on.Contains(r.Outcome)).OrderBy(r => Rank(r.Outcome)))
            VisiblePlan.Add(row);

        OnPropertyChanged(nameof(HiddenNote));
        OnPropertyChanged(nameof(HasHidden));

        // Keep a selection that is still on screen, so the diff pane never shows a hidden row.
        if (SelectedRow is null || !VisiblePlan.Contains(SelectedRow))
            SelectedRow = VisiblePlan.FirstOrDefault(r => r.HasDiff) ?? VisiblePlan.FirstOrDefault();
    }

    /// <summary>Ordering within the plan; OrderBy is stable, so document order survives inside a bucket.</summary>
    private static int Rank(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Failed => 0,
        PlanOutcome.Configure => 1,
        PlanOutcome.Create => 2,
        PlanOutcome.Deferred => 3,
        _ => 4,
    };

    /// <summary>Outcomes that are the plan's background noise: a long plan starts with them folded away.</summary>
    private static bool IsQuiet(PlanOutcome outcome) =>
        outcome is PlanOutcome.Unchanged or PlanOutcome.Deferred;

    /// <summary>Rebuild the chips from a fresh plan, hiding the no-ops when the plan is long.</summary>
    private void BuildBuckets()
    {
        Buckets.Clear();

        foreach (var kind in new[]
                 {
                     PlanOutcome.Create, PlanOutcome.Configure, PlanOutcome.Failed,
                     PlanOutcome.Deferred, PlanOutcome.Unchanged,
                 })
        {
            var count = Plan.Count(r => r.Outcome == kind);
            if (count == 0)
                continue;

            Buckets.Add(new PlanBucket(kind, count, on: !IsQuiet(kind) || Plan.Count <= BigPlan));
        }

        OnPropertyChanged(nameof(HasBuckets));
        ApplyFilter();
    }

    /// <summary>The selected row's unified diff, split for colouring.</summary>
    public ObservableCollection<DiffLineRow> DiffLines { get; } = [];

    [ObservableProperty] private ApplyPlanRow? _selectedRow;

    public bool HasDiff => DiffLines.Count > 0;

    /// <summary>The badge only makes sense once a preview plan exists.</summary>
    public bool ShowDryRunBadge => HasPlan && IsPreview;

    public bool CanApply => HasPlan && IsPreview && !IsBusy && Plan.Any(r => r.IsChange) && !Plan.Any(r => r.IsFailed);

    partial void OnHasPlanChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ShowDryRunBadge));
    }

    partial void OnIsPreviewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ShowDryRunBadge));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    partial void OnYamlTextChanged(string value)
    {
        // The plan describes the text that produced it; editing invalidates it.
        if (!HasPlan)
            return;

        HasPlan = false;
        Plan.Clear();
        VisiblePlan.Clear();
        Buckets.Clear();
        OnPropertyChanged(nameof(HasBuckets));
        ShowDiff(null);
    }

    partial void OnSelectedRowChanged(ApplyPlanRow? value)
    {
        foreach (var row in Plan)
            row.IsSelected = ReferenceEquals(row, value);

        ShowDiff(value);
    }

    [RelayCommand]
    private Task DryRunAsync() => RunAsync(dryRun: true);

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply)
            return;

        await RunAsync(dryRun: false);
        if (Error is null && _onApplied is not null)
            await _onApplied();
    }

    [RelayCommand]
    private void Reset()
    {
        YamlText = SampleManifest;
        Source = "pasted";
        Error = null;
        LastCommand = string.Empty;
        Diagnostics.Clear();
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    private async Task RunAsync(bool dryRun)
    {
        IsBusy = true;
        Error = null;
        Plan.Clear();
        VisiblePlan.Clear();
        Buckets.Clear();
        ShowDiff(null);

        try
        {
            var bundle = new ManifestBundle
            {
                Yaml = YamlText,
                Source = Source,
                DryRun = dryRun,

                // Rendered bundles rarely name a namespace per document; the page says it once.
                Namespace = RenderNamespace.Trim(),
            };
            // Progress<T> posts to the context it was made on, so the engine can report from
            // whatever thread it is running on and Status is still only ever set on this one.
            var status = new Progress<string>(text => Status = text);

            await foreach (var progress in _cluster.ApplyAsync(bundle, status))
                Plan.Add(new ApplyPlanRow(progress));

            IsPreview = dryRun;
            HasPlan = true;
            BuildBuckets();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            HasPlan = false;
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
        }
    }

    private void ShowDiff(ApplyPlanRow? row)
    {
        DiffLines.Clear();
        if (row is not null)
        {
            foreach (var line in row.DiffLines)
                DiffLines.Add(line);
        }

        OnPropertyChanged(nameof(HasDiff));
    }

}

/// <summary>What an apply would do to a resource, as the plan groups it.</summary>
public enum PlanOutcome
{
    Create,
    Configure,
    Unchanged,

    /// <summary>Could not be previewed until the bundle's own namespaces and CRDs exist (KON-380).</summary>
    Deferred,

    Failed,
}

/// <summary>One outcome in the plan: how many, and whether its rows are on screen.</summary>
public sealed partial class PlanBucket : ObservableObject
{
    public PlanBucket(PlanOutcome kind, int count, bool on)
    {
        Kind = kind;
        _isOn = on;

        var (label, colour) = kind switch
        {
            PlanOutcome.Create => ("create", "#34D399"),
            PlanOutcome.Configure => ("change", "#F5B14C"),
            PlanOutcome.Deferred => ("not previewed", "#8A94A2"),
            PlanOutcome.Failed => ("failed", "#F87171"),
            _ => ("unchanged", "#8A94A2"),
        };

        Text = $"{count.ToString(CultureInfo.InvariantCulture)} {label}";
        Accent = new SolidColorBrush(Color.Parse(colour));
    }

    public PlanOutcome Kind { get; }
    public string Text { get; }
    public IBrush Accent { get; }

    [ObservableProperty] private bool _isOn;
}

/// <summary>One resource in an apply plan: what it is, what will happen (or happened), and why.</summary>
public sealed partial class ApplyPlanRow : ObservableObject
{
    public ApplyPlanRow(ApplyProgress progress)
    {
        var action = progress.Action;
        Title = $"{progress.Resource.Kind.Kind}/{progress.Resource.Name}";

        Outcome = action switch
        {
            ApplyAction.Created or ApplyAction.WouldCreate => PlanOutcome.Create,
            ApplyAction.Configured or ApplyAction.WouldChange => PlanOutcome.Configure,
            ApplyAction.Deferred => PlanOutcome.Deferred,
            ApplyAction.Failed => PlanOutcome.Failed,
            _ => PlanOutcome.Unchanged,
        };

        (Glyph, Tag, var colour) = action switch
        {
            ApplyAction.WouldCreate => ("+", "create", Success),
            ApplyAction.Created => ("+", "created", Success),
            ApplyAction.WouldChange => ("~", "configure", Warn),
            ApplyAction.Configured => ("~", "configured", Warn),
            ApplyAction.Deferred => ("?", "not previewed", Faint),
            ApplyAction.Failed => ("!", "failed", Danger),
            _ => ("=", "no change", Faint),
        };

        Accent = new SolidColorBrush(Color.Parse(colour));

        var ns = progress.Resource.Namespace ?? "cluster-scoped";
        Subtitle = action switch
        {
            ApplyAction.WouldCreate => $"{ns} · will be created",
            ApplyAction.Created => $"{ns} · created",
            ApplyAction.WouldChange => $"{ns} · will be configured",
            ApplyAction.Configured => $"{ns} · configured",
            ApplyAction.Deferred => progress.Error ?? $"{ns} · not previewed",
            ApplyAction.Failed => progress.Error ?? $"{ns} · failed",
            _ => $"{ns} · unchanged",
        };

        DiffLines = progress.Diff.Length == 0
            ? []
            : progress.Diff.Split('\n').Select(l => new DiffLineRow(l)).ToList();
    }

    private const string Success = "#34D399";
    private const string Warn = "#F5B14C";
    private const string Danger = "#F87171";
    private const string Faint = "#8A94A2";

    public string Glyph { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Tag { get; }
    public IBrush Accent { get; }

    public PlanOutcome Outcome { get; }

    public bool IsFailed => Outcome == PlanOutcome.Failed;

    /// <summary>Whether this row is something an apply would act on.</summary>
    public bool IsChange => Outcome is PlanOutcome.Create or PlanOutcome.Configure;

    public IReadOnlyList<DiffLineRow> DiffLines { get; }
    public bool HasDiff => DiffLines.Count > 0;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One line of a unified diff, coloured by its marker column.</summary>
public sealed class DiffLineRow
{
    public DiffLineRow(string line)
    {
        Text = line;
        Brush = new SolidColorBrush(Color.Parse(line.Length == 0 ? Context : line[0] switch
        {
            '+' => "#34D399",
            '-' => "#F87171",
            '…' => "#5C6675",
            _ => Context,
        }));
    }

    private const string Context = "#8A94A2";

    public string Text { get; }
    public IBrush Brush { get; }
}
