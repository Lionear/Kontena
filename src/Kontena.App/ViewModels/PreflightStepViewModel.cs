using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// The preflight step: runs KON-235's engine over the wizard's machines and shows what it found
/// (KON-379).
/// <para>
/// The engine already decides everything worth deciding — what blocks, what only warns, what could
/// not be checked, and whether the run may continue. This adds no rules of its own; it turns
/// <see cref="PreflightReport"/> into rows and hands the remedy button back to
/// <see cref="HostPreflight.ApplyAsync"/>.
/// </para>
/// </summary>
public sealed partial class PreflightStepViewModel(
    Func<RemoteClusterHost, IPreflightProbe> probeFor) : ObservableObject
{
    /// <summary>One group per machine, plus the cluster-wide findings under their own heading.</summary>
    public ObservableCollection<PreflightGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private bool _isRunning;

    /// <summary>The last report, or null when it has not been run yet.</summary>
    [ObservableProperty] private PreflightReport? _report;

    /// <summary>Nothing has been checked yet, so there is nothing to show and nothing to conclude.</summary>
    public bool HasRun => Report is not null;

    /// <summary>
    /// Whether the wizard may go on. Straight from the report — this screen does not get a second
    /// opinion about it, and false while nothing has run is the honest default.
    /// </summary>
    public bool CanContinue => Report?.CanContinue == true;

    public string Summary => Report?.Summary ?? "Nothing has been checked yet.";

    /// <summary>
    /// Runs every check over every machine. Kept re-runnable: fixing something and asking again is the
    /// whole loop this screen exists for.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<RemoteClusterHost> hosts, string? cni, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        IsRunning = true;

        try
        {
            Show(await HostPreflight.RunAsync(hosts, probeFor, cni, ct: ct), hosts);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Drops what was found, for when the machines behind it change.</summary>
    public void Clear()
    {
        Groups.Clear();
        Report = null;
        Recompute();
    }

    private void Show(PreflightReport report, IReadOnlyList<RemoteClusterHost> hosts)
    {
        Report = report;
        Groups.Clear();

        foreach (var target in report.Findings.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Null for the cluster-wide group, which is what makes its rows offer no remedy: there is
            // no one machine to run one on.
            var host = hosts.FirstOrDefault(
                h => string.Equals(h.Address, target, StringComparison.OrdinalIgnoreCase));

            Groups.Add(new PreflightGroupViewModel(target, report.For(target), host, ApplyAsync));
        }

        Recompute();
    }

    /// <summary>
    /// Runs a finding's remedy and puts back whatever the re-check said. Re-checked by the engine, not
    /// assumed here: a remedy that exited zero has only been reported by the thing we asked to fix.
    /// </summary>
    private async Task ApplyAsync(PreflightRowViewModel row)
    {
        if (row.Finding.Remedy is null || Report is null || row.Host is not { } host)
            return;

        row.IsApplying = true;

        // Held before the row is updated: the report is rebuilt by identity, and updating the row
        // first would leave nothing in it still pointing at the finding being replaced — so the
        // report would keep the failure the remedy just cleared, and the Continue button with it.
        var previous = row.Finding;

        try
        {
            var updated = await HostPreflight.ApplyAsync(previous, probeFor(host));

            row.Update(updated);
            Report = new PreflightReport(
                [.. Report.Findings.Select(f => ReferenceEquals(f, previous) ? updated : f)]);
        }
        finally
        {
            row.IsApplying = false;
            Recompute();
        }
    }

    private void Recompute()
    {
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>Everything one machine — or the cluster as a whole — had to say.</summary>
public sealed class PreflightGroupViewModel
{
    public PreflightGroupViewModel(
        string target,
        IReadOnlyList<PreflightFinding> findings,
        RemoteClusterHost? host,
        Func<PreflightRowViewModel, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(findings);

        Target = target;
        Rows = [.. findings.Select(f => new PreflightRowViewModel(f, apply) { Host = host })];
    }

    /// <summary>The machine's address, or <c>cluster</c> for what no single machine can answer.</summary>
    public string Target { get; }

    /// <summary>
    /// True for the group that is about the fleet rather than a machine — a duplicate hostname or a
    /// mix of architectures belongs to no one host, so it gets its own heading rather than being
    /// blamed on whichever machine sorted first.
    /// </summary>
    public bool IsCluster => string.Equals(Target, "cluster", StringComparison.OrdinalIgnoreCase);

    public string Title => IsCluster ? "Across the cluster" : Target;

    public IReadOnlyList<PreflightRowViewModel> Rows { get; }
}

/// <summary>One check's answer, with its reason and whatever can be done about it.</summary>
public sealed partial class PreflightRowViewModel(
    PreflightFinding finding, Func<PreflightRowViewModel, Task> apply) : ObservableObject
{
    [ObservableProperty] private PreflightFinding _finding = finding;

    /// <summary>Which machine to run a remedy on, or null for a cluster-wide finding.</summary>
    public RemoteClusterHost? Host { get; init; }

    [ObservableProperty] private bool _isApplying;

    public string Title => Finding.Check.Title;

    /// <summary>Always present, by construction — the engine cannot produce a finding without one.</summary>
    public string Reason => Finding.Reason;

    public PreflightOutcome Outcome => Finding.Outcome;

    public bool IsPassed => Outcome == PreflightOutcome.Passed;
    public bool IsWarned => Outcome == PreflightOutcome.Warned;
    public bool IsFailed => Outcome == PreflightOutcome.Failed;

    /// <summary>Shown apart from failed: "we could not look" is not "we looked and it is wrong".</summary>
    public bool IsUnknown => Outcome == PreflightOutcome.Unknown;

    /// <summary>Whether this row is what is stopping the wizard.</summary>
    public bool Blocks => Finding.Blocks;

    public bool HasRemedy => Finding.Remedy is not null && Host is not null;

    public string RemedyTitle => Finding.Remedy?.Title ?? string.Empty;

    public string RemedyDetail => Finding.Remedy?.Detail ?? string.Empty;

    /// <summary>The command, shown before it runs — this is somebody's machine.</summary>
    public string RemedyCommand => Finding.Remedy?.Command ?? string.Empty;

    [RelayCommand]
    private Task Apply() => apply(this);

    /// <summary>Replaces the finding after a remedy re-check.</summary>
    public void Update(PreflightFinding updated)
    {
        Finding = updated;

        foreach (var name in new[]
                 {
                     nameof(Title), nameof(Reason), nameof(Outcome), nameof(IsPassed), nameof(IsWarned),
                     nameof(IsFailed), nameof(IsUnknown), nameof(Blocks), nameof(HasRemedy),
                     nameof(RemedyTitle), nameof(RemedyDetail), nameof(RemedyCommand),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
