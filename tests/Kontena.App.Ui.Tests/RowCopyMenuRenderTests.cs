using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kontena.App.Controls;
using Kontena.App.ViewModels;
using Kontena.App.Views;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Ui.Tests;

/// <summary>
/// A table row's name can be copied from its context menu (KON-482), without opening the detail page
/// first — by right-click, and from the keyboard on the row's focused name.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class RowCopyMenuRenderTests(HeadlessSessionFixture headless)
{
    [Fact]
    public Task Right_clicking_a_pod_row_offers_its_name_and_its_namespace_qualified_name() =>
        headless.Session.Dispatch(async () =>
        {
            var (window, row) = await PodRowAsync("api-7d9c");
            var clipboard = window.Clipboard ?? throw new InvalidOperationException("no clipboard");
            await clipboard.ClearAsync();

            var point = row.TranslatePoint(new Point(row.Bounds.Width - 4, row.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("the row was never laid out");
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Pump();

            var menu = row.ContextMenu ?? throw new InvalidOperationException("the row has no context menu");
            Assert.True(menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Copy name", "Copy namespace/name"], items.Select(i => i.Header as string));

            items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Pump();

            var copied = await clipboard.TryGetDataAsync() ?? throw new InvalidOperationException("nothing was copied");
            Assert.Equal("app/api-7d9c", await copied.TryGetTextAsync());
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// The keyboard route (KON-56): the row's name is the focusable part of the row, and the platform's
    /// context-menu keys on it open the same menu.
    /// </summary>
    [Theory]
    [InlineData(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10)]
    [InlineData(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu)]
    public Task The_context_menu_keys_on_a_focused_row_open_the_copy_menu(Key key, RawInputModifiers modifiers, PhysicalKey physical) =>
        headless.Session.Dispatch(async () =>
        {
            var (window, row) = await PodRowAsync("api-7d9c");

            var name = row.GetVisualDescendants().OfType<Button>().First();
            Assert.True(name.Focus(NavigationMethod.Tab));
            window.KeyPress(key, modifiers, physical, null);
            Pump();

            Assert.True(row.ContextMenu?.IsOpen);
        }, CancellationToken.None).Unwrap();

    /// <summary>
    /// Every resource table carries the menu on its row template, so it is not a pods-only feature.
    /// Read from the view source, because the row templates only materialise with data behind them;
    /// the binding inside is compiled, so a wrong property is a build error rather than a quiet miss.
    /// A new resource table belongs in this list.
    /// </summary>
    [Theory]
    [InlineData("ClusterPodsView", "PodRow")]
    [InlineData("RelatedPodsView", "PodRow")]
    [InlineData("ClusterWorkloadsView", "WorkloadRow")]
    [InlineData("ClusterServicesView", "ServiceRow")]
    [InlineData("ClusterIngressesView", "IngressRow")]
    [InlineData("ClusterNetworkPoliciesView", "NetworkPolicyRow")]
    [InlineData("ClusterConfigMapsView", "ConfigObjectRow")]
    [InlineData("ClusterSecretsView", "ConfigObjectRow")]
    [InlineData("ClusterPvcsView", "PvcRow")]
    [InlineData("ClusterVolumesView", "PersistentVolumeRow")]
    [InlineData("ClusterStorageClassDetailView", "PersistentVolumeRow")]
    [InlineData("ClusterStorageClassesView", "StorageClassRow")]
    [InlineData("ClusterNamespacesView", "NamespaceRow")]
    [InlineData("ClusterHelmReleasesView", "HelmReleaseRow")]
    [InlineData("ClusterWebhooksView", "AdmissionWebhookRow")]
    [InlineData("ClusterNodesView", "NodeCardRow")]
    [InlineData("ContainersView", "ContainerRowViewModel")]
    [InlineData("ImagesView", "ImageRowViewModel")]
    [InlineData("VolumesView", "VolumeRowViewModel")]
    [InlineData("NetworksView", "NetworkRowViewModel")]
    public void Every_resource_table_row_offers_the_copy_menu(string view, string row)
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view + ".axaml"));
        var template = xaml.IndexOf($"x:DataType=\"vm:{row}\"", StringComparison.Ordinal);
        Assert.True(template >= 0, $"{view} has no {row} template");

        // The row element is the first one inside the template, comments aside.
        var start = Regex.Match(xaml[template..], @"<\w").Index + template;
        var rowTag = xaml[start..xaml.IndexOf('>', start)];
        Assert.Contains("controls:CopyMenu.Name=", rowTag);
    }

    private static string ViewsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var views = Path.Combine(dir.FullName, "src", "Kontena.App", "Views");
            if (Directory.Exists(views))
                return views;
        }

        throw new DirectoryNotFoundException("Could not find src/Kontena.App/Views from the test output directory.");
    }

    private static async Task<(Window Window, Border Row)> PodRowAsync(string pod)
    {
        var page = new ClusterPodsViewModel(new FakeClusterEngine(), "app");
        await page.LoadAsync();

        var window = new Window { Width = 1400, Height = 900, Content = new ClusterPodsView { DataContext = page } };
        window.Show();
        Pump();

        var row = window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.DataContext is PodRow r && r.Name == pod && CopyMenu.GetName(b) is not null);
        return (window, row);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
