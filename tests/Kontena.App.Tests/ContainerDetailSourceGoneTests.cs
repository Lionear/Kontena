using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// KON-308, the container half — see ClusterDetailSourceGoneTests for the five k8s kinds. The page
/// used to learn about a removal only when it performed one itself, so a container killed from a
/// terminal, another window or plain <c>docker rm</c> left a detached window silently stale. It now
/// follows the engine's own event stream, the same one ActivityLog and the container list read.
/// </summary>
public sealed class ContainerDetailSourceGoneTests
{
    private static TerminalFont Font() => new("JetBrains Mono", 13, true);

    private static async Task<(FakeEngine Engine, ContainerSummary Container)> EngineAsync()
    {
        var engine = new FakeEngine();
        var container = (await engine.ListContainersAsync(all: true)).First();

        return (engine, container);
    }

    [Fact]
    public async Task A_removed_event_for_this_container_marks_it_gone()
    {
        var (engine, container) = await EngineAsync();
        using var detail = new ContainerDetailViewModel(engine, container, Font());

        Assert.False(detail.IsSourceGone);

        engine.EmitEvent(new EngineEvent(
            EngineEventType.Removed, ResourceKind.Container, container.Id, DateTimeOffset.UtcNow));

        for (var i = 0; i < 200 && !detail.IsSourceGone; i++)
            await Task.Delay(5);

        Assert.True(detail.IsSourceGone);
    }

    [Fact]
    public async Task A_removed_event_for_a_different_container_does_nothing()
    {
        var (engine, container) = await EngineAsync();
        using var detail = new ContainerDetailViewModel(engine, container, Font());

        engine.EmitEvent(new EngineEvent(
            EngineEventType.Removed, ResourceKind.Container, container.Id + "-someone-else",
            DateTimeOffset.UtcNow));

        await Task.Delay(100);

        Assert.False(detail.IsSourceGone);
    }

    [Fact]
    public async Task A_stop_of_this_container_is_not_a_removal()
    {
        // The stream carries every lifecycle transition; only removal is terminal. Reading a stop as
        // "gone" would put the banner up on a container you are about to start again.
        var (engine, container) = await EngineAsync();
        using var detail = new ContainerDetailViewModel(engine, container, Font());

        engine.EmitEvent(new EngineEvent(
            EngineEventType.Stopped, ResourceKind.Container, container.Id, DateTimeOffset.UtcNow));

        await Task.Delay(100);

        Assert.False(detail.IsSourceGone);
    }
}
