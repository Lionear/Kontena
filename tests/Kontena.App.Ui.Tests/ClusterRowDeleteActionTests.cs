using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// The Delete on a workload, service and ingress row reaches the screen, in the actions cell, bound
/// to something (KON-332).
/// <para>
/// Against the rendered view rather than the view model because two of these rows changed shape and
/// not just content: the ingress row grew an actions column it never had, and the service row's lone
/// Forward became a panel of two. A button placed in the wrong cell of a grid whose column count just
/// changed still compiles and still binds — it lands on top of the AGE text.
/// </para>
/// <para>
/// <b>What this deliberately does not assert:</b> that the pills fit their column. Headless Avalonia
/// draws through a stub that measures text by a fixed advance rather than by the shipped font — it
/// puts a single "Delete" pill at 99px, while the pods page has shipped one in an 84px column since
/// KON-69. Any pixel assertion here would be about the stub, and calibrating real column widths to it
/// would make every actions column too wide in the app. The widths follow the sizes the other pages
/// already use: one pill ≈ 84, two ≈ 140.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ClusterRowDeleteActionTests(HeadlessSessionFixture headless)
{
    private static Window Show(object view)
    {
        // Wide enough that every star column gets a share and the rows lay out as they would on a
        // real window rather than collapsing.
        var window = new Window { Width = 1400, Height = 900, Content = view };

        window.Show();

        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        return window;
    }

    private static List<Button> Deletes(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "Delete")];

    /// <summary>
    /// The Delete is in the row's actions cell — the last column — and was actually laid out.
    /// </summary>
    private static void AssertItSitsInTheActionsCell(Button delete)
    {
        // The pills sit in a StackPanel on the wider rows and directly in the cell on the ingress row.
        var cell = delete.GetVisualAncestors().OfType<Layoutable>()
            .Prepend(delete)
            .First(a => a.GetVisualParent() is Grid { ColumnDefinitions.Count: > 0 });

        var grid = (Grid)cell.GetVisualParent()!;

        Assert.Equal(grid.ColumnDefinitions.Count - 1, Grid.GetColumn((Control)cell));
        Assert.True(delete.Bounds.Width > 0);
    }

    [Fact]
    public Task A_workload_row_shows_Delete_beside_Scale_and_Restart() => headless.Session.Dispatch(
        () =>
        {
            var page = new ClusterWorkloadsViewModel(new FakeClusterEngine(), "app");
            page.LoadAsync().GetAwaiter().GetResult();

            var window = Show(new ClusterWorkloadsView { DataContext = page });
            var deletes = Deletes(window);

            // One per row: the confirm is wired for every workload, whatever its kind.
            Assert.Equal(page.Items.Count, deletes.Count);
            Assert.All(deletes, b => Assert.NotNull(b.Command));

            // The three-pill row — Scale, Restart, Delete — is the crowded one, so it is the one where
            // a Delete in the wrong cell would be least obvious.
            var crowded = deletes[page.Items.IndexOf(page.Items.First(r => r is { CanScale: true, CanRestart: true }))];
            AssertItSitsInTheActionsCell(crowded);
        },
        CancellationToken.None);

    [Fact]
    public Task A_service_row_shows_Delete_beside_Forward() => headless.Session.Dispatch(
        () =>
        {
            var page = new ClusterServicesViewModel(new FakeClusterEngine(), "app");
            page.LoadAsync().GetAwaiter().GetResult();

            var window = Show(new ClusterServicesView { DataContext = page });
            var deletes = Deletes(window);

            Assert.Equal(page.Items.Count, deletes.Count);
            Assert.All(deletes, AssertItSitsInTheActionsCell);
        },
        CancellationToken.None);

    [Fact]
    public Task An_ingress_row_shows_Delete_in_a_column_it_never_had_before() => headless.Session.Dispatch(
        () =>
        {
            var page = new ClusterIngressesViewModel(new FakeClusterEngine(), "app");
            page.LoadAsync().GetAwaiter().GetResult();

            var window = Show(new ClusterIngressesView { DataContext = page });
            var deletes = Deletes(window);

            Assert.Equal(page.Items.Count, deletes.Count);
            Assert.All(deletes, AssertItSitsInTheActionsCell);
        },
        CancellationToken.None);
}
