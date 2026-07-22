using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Core.Orchestration.Models;

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
    public ApplyManifestViewModel(IClusterEngine cluster, string? context, Func<Task>? onApplied = null)
    {
        _cluster = cluster;
        _onApplied = onApplied;
        ContextName = context ?? cluster.Contexts.FirstOrDefault(c => c.IsCurrent)?.Name ?? "cluster";
        _yamlText = SampleManifest;
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

    /// <summary>Set once a dry-run or apply has produced a plan.</summary>
    [ObservableProperty] private bool _hasPlan;

    /// <summary>True while the plan is a preview; false once it reflects a real apply.</summary>
    [ObservableProperty] private bool _isPreview = true;

    /// <summary>"1 change · 1 create" — the rollup beside the plan header.</summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>The plan, one row per document in the bundle.</summary>
    public ObservableCollection<ApplyPlanRow> Plan { get; } = [];

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
        ShowDiff(null);
        Summary = string.Empty;
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
    }

    private async Task RunAsync(bool dryRun)
    {
        IsBusy = true;
        Error = null;
        Plan.Clear();
        ShowDiff(null);

        try
        {
            var bundle = new ManifestBundle { Yaml = YamlText, Source = Source, DryRun = dryRun };
            await foreach (var progress in _cluster.ApplyAsync(bundle))
                Plan.Add(new ApplyPlanRow(progress));

            IsPreview = dryRun;
            HasPlan = true;
            Summary = Describe(Plan);
            SelectedRow = Plan.FirstOrDefault(r => r.HasDiff) ?? Plan.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            HasPlan = false;
        }
        finally
        {
            IsBusy = false;
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

    /// <summary>"2 create · 1 change · 1 unchanged" — only the non-zero buckets.</summary>
    private static string Describe(ObservableCollection<ApplyPlanRow> plan)
    {
        if (plan.Count == 0)
            return "nothing to apply";

        var parts = new List<string>();
        Add(parts, plan.Count(r => r.IsCreate), "create");
        Add(parts, plan.Count(r => r.IsConfigure), "change");
        Add(parts, plan.Count(r => r.IsUnchanged), "unchanged");
        Add(parts, plan.Count(r => r.IsFailed), "failed");
        return string.Join(" · ", parts);

        static void Add(List<string> parts, int count, string label)
        {
            if (count > 0)
                parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {label}");
        }
    }
}

/// <summary>One resource in an apply plan: what it is, what will happen (or happened), and why.</summary>
public sealed partial class ApplyPlanRow : ObservableObject
{
    public ApplyPlanRow(ApplyProgress progress)
    {
        var action = progress.Action;
        Title = $"{progress.Resource.Kind.Kind}/{progress.Resource.Name}";

        IsCreate = action is ApplyAction.Created or ApplyAction.WouldCreate;
        IsConfigure = action is ApplyAction.Configured or ApplyAction.WouldChange;
        IsUnchanged = action is ApplyAction.Unchanged;
        IsFailed = action is ApplyAction.Failed;

        (Glyph, Tag, var colour) = action switch
        {
            ApplyAction.WouldCreate => ("+", "create", Success),
            ApplyAction.Created => ("+", "created", Success),
            ApplyAction.WouldChange => ("~", "configure", Warn),
            ApplyAction.Configured => ("~", "configured", Warn),
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

    public bool IsCreate { get; }
    public bool IsConfigure { get; }
    public bool IsUnchanged { get; }
    public bool IsFailed { get; }

    /// <summary>Whether this row is something an apply would act on.</summary>
    public bool IsChange => IsCreate || IsConfigure;

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
