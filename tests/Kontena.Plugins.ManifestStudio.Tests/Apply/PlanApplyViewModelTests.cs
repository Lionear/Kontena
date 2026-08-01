using System.Runtime.CompilerServices;
using Kontena.Plugins.ManifestStudio.Apply;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests.Apply;

/// <summary>A two-method fake for the same reason <c>FakeClusterSchemaSource</c> is one (KON-288):
/// no thirty-member <c>IClusterEngine</c> stub just to test one command.</summary>
public sealed class FakeApplyTarget : IApplyTarget
{
    public ManifestBundle? LastBundle { get; private set; }
    public Func<ManifestBundle, IAsyncEnumerable<ApplyProgress>>? Respond { get; set; }

    public async IAsyncEnumerable<ApplyProgress> ApplyAsync(
        ManifestBundle bundle, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastBundle = bundle;

        if (Respond is null)
            yield break;

        await foreach (var progress in Respond(bundle).WithCancellation(ct))
            yield return progress;
    }
}

public sealed class PlanApplyViewModelTests
{
    private static readonly ResourceRef Deployment = new(new GroupVersionKind("apps", "v1", "Deployment"), "default", "sample");

    private static OpenDocument DocumentWith(string text)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, text);
        return OpenDocument.Load(path);
    }

    private static async IAsyncEnumerable<ApplyProgress> Single(ApplyProgress progress)
    {
        await Task.Yield();
        yield return progress;
    }

    [Fact]
    public async Task Plan_sends_a_dry_run_bundle_built_from_the_document()
    {
        var target = new FakeApplyTarget();
        var vm = new PlanApplyViewModel(target);
        var document = DocumentWith("kind: Deployment\n");

        await vm.PlanCommand.ExecuteAsync(document);

        Assert.NotNull(target.LastBundle);
        Assert.True(target.LastBundle!.DryRun);
        Assert.Equal("kind: Deployment\n", target.LastBundle.Yaml);
        Assert.Equal(document.Name, target.LastBundle.Source);
    }

    [Fact]
    public async Task Apply_sends_a_real_bundle_not_a_dry_run()
    {
        var target = new FakeApplyTarget();
        var vm = new PlanApplyViewModel(target);

        await vm.ApplyCommand.ExecuteAsync(DocumentWith("kind: Deployment\n"));

        Assert.False(target.LastBundle!.DryRun);
    }

    [Fact]
    public async Task Streamed_progress_lands_in_results_in_order()
    {
        var target = new FakeApplyTarget
        {
            Respond = _ => Single(new ApplyProgress { Resource = Deployment, Action = ApplyAction.WouldCreate }),
        };
        var vm = new PlanApplyViewModel(target);

        await vm.PlanCommand.ExecuteAsync(DocumentWith("kind: Deployment\n"));

        var result = Assert.Single(vm.Results);
        Assert.Equal(ApplyAction.WouldCreate, result.Action);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task A_new_run_clears_the_previous_results()
    {
        var target = new FakeApplyTarget
        {
            Respond = _ => Single(new ApplyProgress { Resource = Deployment, Action = ApplyAction.Unchanged }),
        };
        var vm = new PlanApplyViewModel(target);
        var document = DocumentWith("kind: Deployment\n");

        await vm.PlanCommand.ExecuteAsync(document);
        Assert.Single(vm.Results);

        target.Respond = _ => AsyncEnumerable.Empty<ApplyProgress>();
        await vm.PlanCommand.ExecuteAsync(document);

        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task A_connection_failure_is_held_in_error_not_thrown()
    {
        var target = new FakeApplyTarget { Respond = _ => throw new InvalidOperationException("no cluster") };
        var vm = new PlanApplyViewModel(target);

        await vm.PlanCommand.ExecuteAsync(DocumentWith("kind: Deployment\n"));

        Assert.Equal("no cluster", vm.Error);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Nothing_happens_without_an_active_document()
    {
        var target = new FakeApplyTarget();
        var vm = new PlanApplyViewModel(target);

        await vm.PlanCommand.ExecuteAsync(null);

        Assert.Null(target.LastBundle);
    }
}
