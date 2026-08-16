using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.Adapters.Kubernetes;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the rule editor (KON-210) draws. The wording and the verdicts are covered against the raw
/// view-model in <c>Kontena.App.Tests</c>; what only a render catches is a binding to a resource key
/// or a converter that does not exist, which XAML fails silently rather than throwing on.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class RuleEditorRenderTests(HeadlessSessionFixture headless)
{
    private static async Task<(RuleEditorViewModel Vm, RuleEditorView View)> RenderAsync(RuleTargeting targeting)
    {
        var vm = new RuleEditorViewModel(
            new FakeClusterEngine(), _ => { }, () => Task.FromResult(targeting));

        await vm.Loaded;

        var view = new RuleEditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (vm, view);
    }

    private static IReadOnlyList<string> VisibleText(RuleEditorView view) =>
    [
        .. view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty),
        .. view.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty),
    ];

    private static RuleTargeting Watching(params (string Key, string Value)[] required) => new()
    {
        Scope = RuleNamespaceScope.AllNamespaces,
        PrometheusNamespace = "monitoring",
        RequiredLabels = required.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal),
    };

    [Fact]
    public Task The_manifest_panel_renders_what_would_be_applied() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, view) = await RenderAsync(Watching(("release", "kube-prometheus-stack")));

                vm.AlertName = "AppHighErrorRate";
                vm.Expression = "up == 0";
                vm.ObjectName = "checkout-slo";
                vm.NamespaceName = "monitoring";
                Dispatcher.UIThread.RunJobs();

                var manifest = Assert.Single(VisibleText(view), t => t.StartsWith("apiVersion:", StringComparison.Ordinal));

                Assert.Equal(vm.Manifest, manifest);
                Assert.Contains("release: kube-prometheus-stack", manifest, StringComparison.Ordinal);
                Assert.DoesNotContain("managed-by", manifest, StringComparison.Ordinal);
            },
            CancellationToken.None);

    [Fact]
    public Task The_prefilled_selector_label_renders_without_a_remove() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, view) = await RenderAsync(Watching(("release", "kube-prometheus-stack")));

                Assert.True(vm.HasObjectLabels);

                var removes = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && b.Content as string == "Remove")
                    .ToList();

                Assert.Empty(removes);
                Assert.Contains(VisibleText(view), t => t.Contains("on the object, not the alert", StringComparison.Ordinal));
            },
            CancellationToken.None);

    [Fact]
    public Task The_open_namespace_menu_renders_a_verdict_per_namespace() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, view) = await RenderAsync(Watching());

                vm.OpenNamespaceMenuCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                var text = VisibleText(view);
                Assert.Contains("monitoring", text);
                Assert.Contains("watched", text);
                Assert.Contains("matched by ruleNamespaceSelector", text);
            },
            CancellationToken.None);

    /// <summary>Three buttons, one on — the segmented control the mockup asks for, not a dropdown.</summary>
    [Fact]
    public Task Severity_renders_as_three_buttons_with_one_selected() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (vm, view) = await RenderAsync(Watching());

                var buttons = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("seg") && b.IsEffectivelyVisible)
                    .ToList();

                Assert.Equal(3, buttons.Count);
                Assert.Single(buttons, b => b.Classes.Contains("on"));

                buttons.Single(b => b.Content as string == "critical").Command?.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("critical", vm.Severity);
                Assert.Single(buttons, b => b.Classes.Contains("on"));
            },
            CancellationToken.None);
}
