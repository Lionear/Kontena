using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// That the ingress detail page actually draws (KON-453).
/// <para>
/// Same reason <c>DetailDrawerRenderTests</c> exists: a ContentControl whose template does not
/// resolve renders the ViewLocator's "Not Found:" placeholder rather than failing the build, and a
/// brand new view reached only by convention is exactly where that goes unnoticed. The YAML tab gets
/// its own case because it is the half of the report that started this ticket.
/// </para>
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class IngressDetailRenderTests(HeadlessSessionFixture headless)
{
    private static (Window Window, ClusterIngressDetailViewModel Detail) OpenDrawer()
    {
        var cluster = new FakeClusterEngine();
        var ingress = cluster.ListIngressesAsync("app").AsTask().GetAwaiter().GetResult()[0];

        var detail = new ClusterIngressDetailViewModel(cluster, ingress);
        var window = new MainWindow { DataContext = new MainWindowViewModel { Detail = detail } };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, detail);
    }

    [Fact]
    public Task The_drawer_shows_the_ingress_page_and_not_the_view_locator_placeholder() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, _) = OpenDrawer();

                Assert.Single(window.GetVisualDescendants().OfType<ClusterIngressDetailView>());
                Assert.DoesNotContain(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.StartsWith("Not Found:", StringComparison.Ordinal) == true);
            },
            CancellationToken.None);

    [Fact]
    public Task The_overview_draws_the_rules_the_list_could_only_fit_in_a_tooltip() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, _) = OpenDrawer();

                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text)
                    .ToList();

                Assert.Contains("app.example.com", texts);
                Assert.Contains("web:80", texts);
                Assert.Contains("web-tls", texts);
            },
            CancellationToken.None);

    [Fact]
    public Task The_yaml_tab_reaches_the_manifest_editor() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, detail) = OpenDrawer();

                Assert.Empty(window.GetVisualDescendants().OfType<ObjectYamlView>()
                    .Where(v => v.IsVisible));

                detail.SelectTabCommand.Execute("yaml");
                Dispatcher.UIThread.RunJobs();

                var yaml = Assert.Single(window.GetVisualDescendants().OfType<ObjectYamlView>());
                Assert.True(yaml.IsVisible);
                Assert.NotEmpty(yaml.GetVisualDescendants().OfType<ManifestEditorView>());
            },
            CancellationToken.None);
}
