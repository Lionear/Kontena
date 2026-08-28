using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Apply;

/// <summary>
/// Drives one open document through the existing plan/apply flow (KON-294 — "plan and apply worden
/// hergebruikt, niet opnieuw bedacht", Plan §1). "Plan" is a dry run: <see cref="ManifestBundle.DryRun"/>
/// true, nothing persisted. "Apply" is the same call with it false.
/// <para>
/// Errors surfacing from a failed connection are held in <see cref="Error"/> rather than thrown out of
/// the command — a plugin command that faults silently disables itself in most MVVM toolkits, which
/// reads to a user as "the button stopped working" rather than "the apply failed".
/// </para>
/// </summary>
public sealed partial class PlanApplyViewModel(IApplyTarget target) : ObservableObject
{
    public ObservableCollection<ApplyProgress> Results { get; } = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _error;

    /// <summary>Whether the results on screen came from a dry run. The notice above the list says
    /// "nothing has been applied" on the strength of this, so it has to be a fact about the run that
    /// produced these rows — not about the button last pressed.</summary>
    [ObservableProperty]
    private bool _wasDryRun;

    /// <summary>"1 create · 1 update · 1 unchanged", for the strip under the list. Counted from the
    /// results rather than predicted, the same reason <c>GitViewModel</c> re-reads status after every
    /// command.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Whether there is anything to show yet, so the page can say so instead of rendering an
    /// empty box.</summary>
    public bool HasResults => Results.Count > 0;

    [RelayCommand]
    private Task Plan(OpenDocument? document) => RunAsync(document, dryRun: true);

    [RelayCommand]
    private Task Apply(OpenDocument? document) => RunAsync(document, dryRun: false);

    private async Task RunAsync(OpenDocument? document, bool dryRun)
    {
        if (document is null)
            return;

        IsRunning = true;
        Error = null;
        Results.Clear();
        Summary = string.Empty;
        WasDryRun = dryRun;

        try
        {
            var bundle = new ManifestBundle { Yaml = document.Text, Source = document.Name, DryRun = dryRun };

            await foreach (var progress in target.ApplyAsync(bundle))
                Results.Add(progress);

            Summary = Summarise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(HasResults));
        }
    }

    private string Summarise()
    {
        var counts = Results
            .GroupBy(r => r.Action)
            .Select(g => $"{g.Count()} {Describe(g.Key)}")
            .ToArray();

        return counts.Length == 0 ? "Nothing in this document" : string.Join(" · ", counts);
    }

    private static string Describe(ApplyAction action) => action switch
    {
        ApplyAction.Created or ApplyAction.WouldCreate => "create",
        ApplyAction.Configured or ApplyAction.WouldChange => "update",
        ApplyAction.Unchanged => "unchanged",
        ApplyAction.Deferred => "deferred",
        ApplyAction.Failed => "failed",
        _ => action.ToString().ToLowerInvariant(),
    };
}
