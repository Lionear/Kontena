using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the alert-detail drawer (KON-208) actually draws its body and its footer jumps. The content
/// itself — matcher building, silence gating — is covered in <c>Kontena.App.Tests</c>; what only a
/// rendered page can catch is a footer button bound to nothing, or a section that never becomes
/// visible because its <c>IsVisible</c> binding was spelled differently in the axaml.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class AlertDetailRenderTests(HeadlessSessionFixture headless)
{
    private static async Task<(AlertDetailViewModel Detail, AlertDetailView View, IReadOnlyList<string> Text)> RenderAsync()
    {
        var source = new FakeAlertSource();
        var alert = (await source.ListAlertsAsync()).Single(a => a.Labels.GetValueOrDefault("pod") == "redis-7d9c4f-x2ktp");
        var rule = (await source.ListRulesAsync()).Single(r => r.Name == alert.Name);

        var detail = new AlertDetailViewModel(
            alert, rule, silence: null,
            onOpenPod: (_, _) => Task.FromResult(true), onSilence: _ => { });
        var view = new AlertDetailView { DataContext = detail };

        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (detail, view,
        [
            .. view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? string.Empty),
        ]);
    }

    [Fact]
    public Task The_body_shows_state_labels_and_expression() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (_, _, text) = await RenderAsync();

                Assert.Contains("KubePodCrashLooping", text);
                Assert.Contains("firing", text);
                Assert.Contains(text, t => t.Contains("alertname=KubePodCrashLooping", StringComparison.Ordinal));
                Assert.Contains(text, t => t.Contains("pod=redis-7d9c4f-x2ktp", StringComparison.Ordinal));
                Assert.Contains(text, t => t.Contains("increase(kube_pod_container_status_restarts_total", StringComparison.Ordinal));

                // Not silenced, so the default-expiry sentence is what tells someone what "Silence…"
                // is about to do (KON-204 §5).
                Assert.Contains(text, t => t.Contains("Expires by default in 2h", StringComparison.Ordinal));
            },
            CancellationToken.None);

    [Fact]
    public Task Every_footer_jump_this_alert_carries_is_a_wired_button() =>
        headless.Session.Dispatch(
            async () =>
            {
                var (detail, view, _) = await RenderAsync();

                Assert.True(detail.CanOpenRunbook);
                Assert.True(detail.CanOpenPod);
                Assert.True(detail.CanOpenLogs);
                Assert.True(detail.CanOpenGraph);

                var buttons = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible)
                    .ToList();

                foreach (var label in new[] { "Runbook", "Pod redis-7d9c4f-x2ktp", "Logs", "Graph in Prometheus", "Silence…" })
                {
                    var button = Assert.Single(buttons, b => b.Content as string == label);
                    Assert.NotNull(button.Command);
                }
            },
            CancellationToken.None);
}
