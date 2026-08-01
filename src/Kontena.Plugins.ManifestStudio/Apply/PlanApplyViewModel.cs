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

        try
        {
            var bundle = new ManifestBundle { Yaml = document.Text, Source = document.Name, DryRun = dryRun };

            await foreach (var progress in target.ApplyAsync(bundle))
                Results.Add(progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
