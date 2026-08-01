using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.Plugins.ManifestStudio.Apply;
using Kontena.Plugins.ManifestStudio.Tests.Apply;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>KON-294 wired to a real, rendered view — clicking Plan actually reaches the target and
/// the result actually reaches the list, through the real button-click pipeline.</summary>
[Collection(HeadlessTests.Name)]
public sealed class PlanApplyViewTests(HeadlessSessionFixture headless)
{
    private static readonly ResourceRef Deployment =
        new(new GroupVersionKind("apps", "v1", "Deployment"), "default", "sample");

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static async IAsyncEnumerable<ApplyProgress> Single(ApplyProgress progress)
    {
        await Task.Yield();
        yield return progress;
    }

    private static OpenDocument DocumentWith(string text)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, text);
        return OpenDocument.Load(path);
    }

    [Fact]
    public Task Clicking_plan_streams_a_result_into_the_list() =>
        headless.Session.Dispatch<object?>(
            async () =>
            {
                var target = new FakeApplyTarget
                {
                    Respond = _ => Single(new ApplyProgress { Resource = Deployment, Action = ApplyAction.WouldCreate }),
                };
                var vm = new PlanApplyViewModel(target);
                var view = new PlanApplyView { DataContext = vm, Document = DocumentWith("kind: Deployment\n") };
                var window = new Window { Width = 500, Height = 400, Content = view };
                window.Show();
                Settle();

                var plan = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Plan"));

                // A raised Click event only reaches handlers subscribed to it — Button invokes its
                // bound Command from inside OnClick() itself, which only a real pointer interaction
                // triggers. Hence an actual simulated click, not RaiseEvent(ClickEvent).
                var center = plan.TranslatePoint(new Point(plan.Bounds.Width / 2, plan.Bounds.Height / 2), window)
                    ?? default;
                window.MouseMove(center, RawInputModifiers.None);
                window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);

                // The command is async (streams from the fake); give it a turn to actually complete.
                await Task.Delay(50);
                Settle();

                var items = view.GetVisualDescendants().OfType<ListBox>().Single().GetVisualDescendants()
                    .OfType<TextBlock>().Select(t => t.Text).ToArray();

                Assert.Contains("WouldCreate", items);
                Assert.Contains("Deployment", items);
                Assert.Contains("sample", items);

                return null;
            },
            CancellationToken.None);
}
