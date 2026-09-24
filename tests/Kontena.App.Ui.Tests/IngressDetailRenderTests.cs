using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

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
    private static (Window Window, ClusterIngressDetailViewModel Detail) OpenDrawer(Ingress? ingress = null)
    {
        var cluster = new FakeClusterEngine();
        ingress ??= cluster.ListIngressesAsync("app").AsTask().GetAwaiter().GetResult()[0];

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

    /// <summary>
    /// The header's open affordance (KON-461). Rendered rather than only projected because which of
    /// the two buttons is on screen is an <c>IsVisible</c> binding, and that is the half a viewmodel
    /// test cannot see.
    /// </summary>
    [Fact]
    public Task A_single_host_gets_one_button_carrying_that_rules_url() =>
        headless.Session.Dispatch(
            () =>
            {
                // The fake's ingress: one rule on app.example.com, covered by web-tls.
                var (window, detail) = OpenDrawer();

                Assert.True(detail.HasOneLink);
                Assert.Equal("https://app.example.com/", detail.OnlyLink?.Url);

                var open = Assert.Single(
                    window.GetVisualDescendants().OfType<Button>(),
                    b => b.IsVisible && ReferenceEquals(b.Command, detail.OnlyLink?.OpenCommand));

                // No flyout: one host does not get a menu with one entry in it.
                Assert.Null(open.Flyout);
            },
            CancellationToken.None);

    [Fact]
    public Task Several_hosts_get_a_flyout_with_an_entry_per_host_and_path() =>
        headless.Session.Dispatch(
            () =>
            {
                var ingress = new Ingress
                {
                    Name = "shop",
                    Namespace = "app",
                    Class = "nginx",
                    Rules =
                    [
                        new IngressRule("shop.example.com", "/", "web", 80),
                        new IngressRule("shop.example.com", "/api", "api", 8080),
                        new IngressRule("admin.example.com", "/", "admin", 80),
                    ],
                    Tls = [new IngressTls("shop-tls", ["shop.example.com"])],
                };

                var (window, detail) = OpenDrawer(ingress);

                Assert.Equal(
                    ["https://shop.example.com/", "https://shop.example.com/api", "http://admin.example.com/"],
                    detail.Links.Select(l => l.Url));

                // Scoped to the page: the shell around it carries flyout buttons of its own.
                var page = Assert.Single(window.GetVisualDescendants().OfType<ClusterIngressDetailView>());

                // The flyout's items are built on open, so open it the way a click does.
                var dropdown = Assert.Single(
                    page.GetVisualDescendants().OfType<Button>(),
                    b => b.IsVisible && b.Flyout is not null);

                dropdown.Flyout!.ShowAt(dropdown);
                Dispatcher.UIThread.RunJobs();

                var entries = page.GetVisualDescendants().OfType<Button>()
                    .Concat(dropdown.Flyout is Flyout { Content: Control c }
                        ? c.GetVisualDescendants().OfType<Button>()
                        : [])
                    .Where(b => b.Command is not null && detail.Links.Any(l => ReferenceEquals(l.OpenCommand, b.Command)))
                    .Select(b => b.Command)
                    .Distinct()
                    .ToList();

                Assert.Equal(3, entries.Count);
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
