using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the "prev" block (KON-209) actually draws its three states. The chip colour/text mapping is
/// covered against the raw view-model in <c>Kontena.App.Tests</c>; what only a render can catch is a
/// binding to a resource key that does not exist, which XAML fails silently rather than throwing on.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class PromqlCheckRenderTests(HeadlessSessionFixture headless)
{
    private static async Task<(PromqlCheckViewModel Vm, IReadOnlyList<string> Text)> RenderAsync(string expression)
    {
        var vm = new PromqlCheckViewModel(new FakeAlertSource()) { Debounce = TimeSpan.Zero };
        var view = new PromqlCheckView { DataContext = vm };

        var window = new Window { Content = view, Width = 500, Height = 220 };
        window.Show();

        vm.Expression = expression;
        await vm.Settled;
        Dispatcher.UIThread.RunJobs();

        return (vm,
        [
            .. view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? string.Empty),
        ]);
    }

    [Fact]
    public Task A_broken_expression_renders_the_error_chip_and_Prometheus_own_message() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, text) = await RenderAsync("sum(rate(foo{job=\"x\"[5m]))");

                Assert.True(vm.HasError);
                Assert.Contains("error", text);
                Assert.Contains(text, t => t.Contains("parse error", StringComparison.Ordinal));
            },
            CancellationToken.None);

    [Fact]
    public Task Zero_matching_series_still_renders_as_parses_with_the_warning_in_the_summary() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, text) = await RenderAsync("up{jobb=\"checkout\"}");

                Assert.True(vm.MatchesNothing);
                Assert.Contains("parses", text);
                Assert.Contains(text, t => t.Contains("0 series match", StringComparison.Ordinal));
            },
            CancellationToken.None);

    [Fact]
    public Task Matching_series_render_with_their_labels_and_values() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (_, text) = await RenderAsync("sum(rate(http_requests_total{job=\"checkout\"}[5m]))");

                Assert.Contains("parses", text);
                Assert.Contains(text, t => t.Contains("checkout-6b4-d92wq", StringComparison.Ordinal));
                Assert.Contains("0.071", text);
            },
            CancellationToken.None);

    [Fact]
    public Task An_empty_expression_renders_nothing() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, text) = await RenderAsync(string.Empty);

                Assert.False(vm.HasResult);
                Assert.DoesNotContain("parses", text);
                Assert.DoesNotContain("error", text);
            },
            CancellationToken.None);
}
