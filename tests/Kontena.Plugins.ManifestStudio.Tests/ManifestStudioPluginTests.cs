using System.Text.Json;
using Avalonia.Controls;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Sdk;
using Kontena.Sdk.Orchestration;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// The entry point (KON-296). What is worth testing here is not that three pages exist but that each
/// one is honest without the thing it needs: no cluster, no workspace, no folder picked yet — all
/// ordinary states, none of them a blank panel or a throw into the shell.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ManifestStudioPluginTests(HeadlessSessionFixture headless)
{
    private sealed class NoHost : IPluginHost
    {
        public IClusterEngine? Cluster => null;
    }

    private static readonly IPluginHost Host = new NoHost();

    [Fact]
    public void The_manifest_matches_the_file_the_loader_reads()
    {
        // The loader rejects a plugin whose code disagrees with its plugin.json, so these two drifting
        // apart does not break a test somewhere — it stops the plugin loading at all.
        var manifest = new ManifestStudioPlugin().Manifest;
        using var file = JsonDocument.Parse(File.ReadAllText(PluginJsonPath()));
        var declared = file.RootElement;

        Assert.Equal(declared.GetProperty("id").GetString(), manifest.Id);
        Assert.Equal(declared.GetProperty("version").GetString(), manifest.Version);
        Assert.Equal(declared.GetProperty("minSdkVersion").GetString(), manifest.MinSdkVersion);
        Assert.Equal("Kontena.Plugins.ManifestStudio.dll", declared.GetProperty("assembly").GetString());
    }

    [Fact]
    public void The_manifest_file_says_what_the_plugin_will_do()
    {
        // The consent dialog renders these. An empty list would be a dialog that asks the user to trust
        // a plugin without telling them what for.
        using var file = JsonDocument.Parse(File.ReadAllText(PluginJsonPath()));

        Assert.NotEmpty(file.RootElement.GetProperty("permissions").EnumerateArray());
    }

    [Fact]
    public void It_contributes_the_three_pages()
    {
        var pages = new ManifestStudioPlugin().GetPages().ToList();

        Assert.Equal(["editor", "plan", "source"], pages.Select(p => p.Key).ToArray());
        Assert.All(pages, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
    }

    [Fact]
    public Task The_editor_opens_without_a_cluster() =>
        headless.Session.Dispatch(
            () =>
            {
                // Plan §3: the bundled schemas are the fallback, so writing a manifest is possible
                // before there is anything to apply it to.
                var view = Assert.IsType<WorkspaceView>(Page("editor").CreateView(Host));

                Assert.NotNull(view.Schemas);
            },
            CancellationToken.None);

    [Theory]
    [InlineData("plan")]
    [InlineData("source")]
    public Task A_page_that_needs_a_workspace_says_so_instead_of_showing_nothing(string key) =>
        headless.Session.Dispatch(
            () =>
            {
                var text = Assert.IsType<TextBlock>(Page(key).CreateView(Host));

                Assert.Contains("Editor", text.Text, StringComparison.Ordinal);
            },
            CancellationToken.None);

    [Fact]
    public Task Reopening_the_editor_keeps_the_schemas_it_already_fetched() =>
        headless.Session.Dispatch(
            () =>
            {
                // Each page is built fresh on navigation, but the session lives on the plugin instance.
                // A new SchemaIndex per navigation would refetch every OpenAPI document the first time
                // you typed on each page — and would drop the workspace you opened along with it.
                var plugin = new ManifestStudioPlugin();
                var editor = plugin.GetPages().Single(p => p.Key == "editor");

                var first = (WorkspaceView)editor.CreateView(Host);
                var second = (WorkspaceView)editor.CreateView(Host);

                Assert.Same(first.Schemas, second.Schemas);
            },
            CancellationToken.None);

    private static PluginPage Page(string key) =>
        new ManifestStudioPlugin().GetPages().Single(p => p.Key == key);

    /// <summary>The manifest as it sits beside the built assembly — the copy the loader would read.</summary>
    private static string PluginJsonPath() => Path.Combine(AppContext.BaseDirectory, "plugin.json");
}
