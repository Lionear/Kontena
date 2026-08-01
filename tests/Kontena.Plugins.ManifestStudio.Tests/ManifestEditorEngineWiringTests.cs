using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// KON-290/291 proved <c>CompletionEngine</c> and <c>ManifestDiagnostics</c> correct as pure functions;
/// this proves they are actually wired to <see cref="ManifestEditorView"/> — typing in the real,
/// rendered editor opens a real completion window, and an invalid document actually populates
/// diagnostics, through the real AvaloniaEdit event pipeline (<c>window.KeyTextInput</c>), not by
/// calling private methods directly.
/// </summary>
[Collection(HeadlessTests.Name)]
public sealed class ManifestEditorEngineWiringTests(HeadlessSessionFixture headless)
{
    private const string Fixture = """
    {
      "components": {
        "schemas": {
          "test.Deployment": {
            "type": "object",
            "required": ["spec"],
            "properties": {
              "apiVersion": { "type": "string" },
              "kind": { "type": "string" },
              "spec": { "$ref": "#/components/schemas/test.DeploymentSpec" }
            },
            "x-kubernetes-group-version-kind": [{ "group": "apps", "version": "v1", "kind": "Deployment" }]
          },
          "test.DeploymentSpec": {
            "type": "object",
            "required": ["selector"],
            "properties": {
              "replicas": { "type": "integer" },
              "selector": { "type": "object" }
            }
          }
        }
      }
    }
    """;

    private static readonly JsonSchemaNode DeploymentSchema =
        OpenApiV3Document.Parse(Fixture).Resolve(new GroupVersionKind("apps", "v1", "Deployment"))!;

    private static (Window Window, ManifestEditorView View) Show(string text, JsonSchemaNode? schema)
    {
        var view = new ManifestEditorView { Text = text, Schema = schema };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        window.Activate();
        Settle();
        view.Editor.TextArea.Focus();
        Settle();
        return (window, view);
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [Fact]
    public Task Typing_opens_a_completion_window_with_a_matching_field() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, view) = Show(string.Empty, DeploymentSchema);

                window.KeyTextInput("a");
                Settle();

                Assert.NotNull(view.CompletionWindow);
                var data = Assert.Single(view.CompletionWindow!.CompletionList.CompletionData);
                Assert.Equal("apiVersion", data.Text);
            },
            CancellationToken.None);

    [Fact]
    public Task Accepting_a_suggestion_replaces_the_partially_typed_word() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, view) = Show(string.Empty, DeploymentSchema);

                window.KeyTextInput("a");
                Settle();

                var completionWindow = view.CompletionWindow!;
                var data = completionWindow.CompletionList.CompletionData[0];
                completionWindow.CompletionList.SelectedItem = data;
                completionWindow.CompletionList.RequestInsertion(EventArgs.Empty);
                Settle();

                Assert.Equal("apiVersion", view.Text);
            },
            CancellationToken.None);

    [Fact]
    public Task No_schema_means_typing_never_opens_a_completion_window() =>
        headless.Session.Dispatch(
            () =>
            {
                var (window, view) = Show(string.Empty, schema: null);

                window.KeyTextInput("a");
                Settle();

                Assert.Null(view.CompletionWindow);
            },
            CancellationToken.None);

    [Fact]
    public Task A_document_missing_a_required_field_populates_diagnostics() =>
        headless.Session.Dispatch(
            () =>
            {
                var (_, view) = Show("apiVersion: apps/v1\nkind: Deployment\nspec:\n  replicas: 3\n", DeploymentSchema);

                var diagnostic = Assert.Single(view.Diagnostics);
                Assert.Equal(DiagnosticAuthority.Schema, diagnostic.Authority);
                Assert.Contains("selector", diagnostic.Message, StringComparison.Ordinal);
            },
            CancellationToken.None);

    [Fact]
    public Task Fixing_the_document_clears_the_diagnostic() =>
        headless.Session.Dispatch(
            () =>
            {
                var (_, view) = Show("apiVersion: apps/v1\nkind: Deployment\nspec:\n  replicas: 3\n", DeploymentSchema);
                Assert.NotEmpty(view.Diagnostics);

                view.Text = "apiVersion: apps/v1\nkind: Deployment\nspec:\n  selector: {}\n";
                Settle();

                Assert.Empty(view.Diagnostics);
            },
            CancellationToken.None);
}
