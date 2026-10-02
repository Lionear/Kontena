using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// KON-477: a workload's page says what autoscales it and which disruption budget covers its pods —
/// and says so when there is none, because "is there a PDB?" is the question.
/// </summary>
public sealed class WorkloadScalingTests
{
    private static async Task<ClusterWorkloadDetailViewModel> DetailAsync(string name)
    {
        var engine = new FakeClusterEngine();
        var workload = (await engine.ListWorkloadsAsync(ns: "app")).Single(w => w.Name == name);
        var page = new ClusterWorkloadDetailViewModel(engine, workload);

        // Loaded fire-and-forget from the constructor; the notes stay null until it has answered.
        for (var i = 0; i < 50 && page.NoAutoscalerNote is null && page.Autoscalers.Count == 0; i++)
            await Task.Delay(5);

        return page;
    }

    [Fact]
    public async Task An_autoscaled_deployment_shows_its_autoscaler_and_no_budget()
    {
        using var api = await DetailAsync("api");

        var hpa = Assert.Single(api.Autoscalers);
        Assert.Equal(("api", "3", "3", "2", "10"), (hpa.Name, hpa.Current, hpa.Desired, hpa.Min, hpa.Max));
        Assert.Contains("cpu: 45% / 70%", hpa.Metrics, StringComparison.Ordinal);
        Assert.Null(api.NoAutoscalerNote);

        Assert.Empty(api.DisruptionBudgets);
        Assert.NotNull(api.NoDisruptionBudgetNote);
    }

    [Fact]
    public async Task A_statefulset_shows_the_budget_that_blocks_its_eviction()
    {
        using var postgres = await DetailAsync("postgres");

        var pdb = Assert.Single(postgres.DisruptionBudgets);
        Assert.Equal(("postgres-pdb", "MIN AVAILABLE", "1", "1 / 1"), (pdb.Name, pdb.BudgetLabel, pdb.Budget, pdb.Healthy));
        Assert.StartsWith("0", pdb.Allowed, StringComparison.Ordinal);

        Assert.Empty(postgres.Autoscalers);
        Assert.NotNull(postgres.NoAutoscalerNote);
    }

    [Fact]
    public async Task Kinds_that_do_not_scale_have_no_section()
    {
        var engine = new FakeClusterEngine();
        var exporter = (await engine.ListWorkloadsAsync(WorkloadKind.DaemonSet)).First();
        using var page = new ClusterWorkloadDetailViewModel(engine, exporter);

        Assert.False(page.ShowScaling);
    }
}
