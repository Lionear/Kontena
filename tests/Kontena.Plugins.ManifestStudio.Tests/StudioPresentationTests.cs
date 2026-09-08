using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Plugins.ManifestStudio.Schemas;
using Kontena.Plugins.ManifestStudio.Views;
using Kontena.Plugins.ManifestStudio.Workspace;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Tests;

/// <summary>
/// The logic behind KON-427's screens. Nothing here measures a colour or a pixel — <c>_brain</c> is
/// explicit that headless Avalonia's text metrics are a stub, and a design that has to be re-approved
/// by a test is a design nobody can change. What is tested is the part that can be silently wrong: how
/// a tree flattens, which file a git badge lands on, what a diff line is, and whether a quick fix edits
/// the document it claims to.
/// </summary>
public sealed class StudioPresentationTests
{
    [Fact]
    public void The_file_pane_flattens_folders_and_files_in_reading_order()
    {
        var root = Directory.CreateTempSubdirectory("manifest-studio-rows-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "deployment.yaml"), "kind: Deployment\n");
        File.WriteAllText(Path.Combine(root, "README.md"), "hi\n");

        try
        {
            var rows = TreeRow.Flatten(ManifestWorkspace.Open(root).Root);

            Assert.Equal(["base", "deployment.yaml", "README.md"], rows.Select(r => r.Name));
            Assert.Equal([true, false, false], rows.Select(r => r.IsFolder));

            // The child is one level in; its siblings at the root are not.
            Assert.Equal(0, rows[0].Indent.Left);
            Assert.True(rows[1].Indent.Left > rows[0].Indent.Left);
            Assert.Equal(0, rows[2].Indent.Left);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_git_badge_lands_on_the_file_git_named_and_on_no_other()
    {
        var root = Directory.CreateTempSubdirectory("manifest-studio-badges-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "deployment.yaml"), "kind: Deployment\n");
        File.WriteAllText(Path.Combine(root, "base", "service.yaml"), "kind: Service\n");

        try
        {
            var workspace = new WorkspaceViewModel(ManifestWorkspace.Open(root));

            // Repository-relative and forward-slashed, which is how git reports it on every platform.
            workspace.SetGitStatus(new GitStatus("main", 0, 0, [new GitFileChange("base/deployment.yaml", "Modified")]));

            var deployment = workspace.Rows.Single(r => r.Name == "deployment.yaml");
            var service = workspace.Rows.Single(r => r.Name == "service.yaml");

            Assert.True(deployment.HasBadge);
            Assert.Equal("M", deployment.Badge);
            Assert.True(deployment.IsModified);
            Assert.False(service.HasBadge);

            // A folder that happens to share a name with a changed path is still a folder.
            Assert.False(workspace.Rows.Single(r => r.Name == "base").HasBadge);

            // Null is "we do not know", and stale badges are worse than none.
            workspace.SetGitStatus(null);
            Assert.False(deployment.HasBadge);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Opening_a_document_marks_its_row_and_its_tab_and_unmarks_the_previous_one()
    {
        var root = Directory.CreateTempSubdirectory("manifest-studio-active-").FullName;
        var first = Path.Combine(root, "a.yaml");
        var second = Path.Combine(root, "b.yaml");
        File.WriteAllText(first, "kind: Service\n");
        File.WriteAllText(second, "kind: Service\n");

        try
        {
            var workspace = new WorkspaceViewModel(ManifestWorkspace.Open(root));
            var a = workspace.Open(first);
            var b = workspace.Open(second);

            Assert.True(b.IsActive);
            Assert.False(a.IsActive);
            Assert.True(workspace.Rows.Single(r => r.Name == "b.yaml").IsActive);
            Assert.False(workspace.Rows.Single(r => r.Name == "a.yaml").IsActive);
            Assert.Equal("b.yaml", workspace.ActivePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("+ replicas: 6", true, false, false)]
    [InlineData("- replicas: 4", false, true, false)]
    [InlineData("@@ spec", false, false, true)]
    [InlineData("+++ b/deployment.yaml", false, false, true)]
    [InlineData("  image: nginx", false, false, false)]
    public void A_diff_line_is_sorted_by_the_sign_it_starts_with(string text, bool add, bool delete, bool header)
    {
        var line = new DiffLine(text);

        Assert.Equal(add, line.IsAdd);
        Assert.Equal(delete, line.IsDelete);
        Assert.Equal(header, line.IsHeader);
    }

    [Fact]
    public void A_plan_says_what_it_would_do_and_an_apply_says_what_it_did()
    {
        Assert.Equal("Create", Label(ApplyAction.WouldCreate));
        Assert.Equal("Created", Label(ApplyAction.Created));
        Assert.Equal("Update", Label(ApplyAction.WouldChange));
        Assert.Equal("Updated", Label(ApplyAction.Configured));

        Assert.True(Group(ApplyAction.WouldCreate, "created"));
        Assert.False(Group(ApplyAction.WouldCreate, "changed"));
        Assert.True(Group(ApplyAction.Failed, "failed"));

        static string Label(ApplyAction action) =>
            (string)ApplyActionConverter.Instance.Convert(action, typeof(string), "label", null!);

        static bool Group(ApplyAction action, string group) =>
            (bool)ApplyActionConverter.Instance.Convert(action, typeof(bool), group, null!);
    }

    [Theory]
    [InlineData(0, 0, "up to date")]
    [InlineData(2, 0, "2 ahead")]
    [InlineData(0, 1, "1 behind")]
    [InlineData(2, 1, "2 ahead · 1 behind")]
    public void The_branch_card_says_sync_state_in_one_line(int ahead, int behind, string expected)
    {
        var status = new GitStatus("main", ahead, behind, []);

        Assert.Equal(expected, status.SyncLabel);
        Assert.Equal(behind > 0, status.IsBehind);
    }

    [Fact]
    public void A_problem_names_the_authority_that_found_it()
    {
        var problem = new Problem(
            new Diagnostic(DiagnosticAuthority.ClusterDiscovery, DiagnosticSeverity.Warning, 0, "Deprecated."),
            []);

        // Line 0 is what the engine counts from; a person counts from 1.
        Assert.Equal("line 1 · cluster discovery", problem.Location);
        Assert.Equal("Warning", problem.SeverityLabel);
        Assert.False(problem.HasFix);
    }
}

/// <summary>The two things that need a real Avalonia application: the YAML definition parses, and a
/// quick fix actually edits the document.</summary>
[Collection(HeadlessTests.Name)]
public sealed class StudioEditorTests(HeadlessSessionFixture headless)
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>The definition is embedded, parsed by hand and named by a string — three ways to be
    /// silently absent, and the only place that would notice is the running app.</summary>
    [Fact]
    public Task The_bundled_yaml_definition_loads_and_carries_a_colour_per_theme() =>
        headless.Session.Dispatch(
            () =>
            {
                var definition = YamlHighlighting.For(ThemeVariant.Dark);

                Assert.Equal("YAML", definition.Name);
                var dark = definition.GetNamedColor("Key")!.Foreground!.GetBrush(null!).ToString();

                YamlHighlighting.For(ThemeVariant.Light);
                var light = definition.GetNamedColor("Key")!.Foreground!.GetBrush(null!).ToString();

                Assert.NotEqual(dark, light);
            },
            CancellationToken.None);

    [Fact]
    public Task A_quick_fix_removes_the_field_it_names_and_leaves_the_rest_of_the_document_alone() =>
        headless.Session.Dispatch(
            () =>
            {
                var view = new ManifestEditorView();
                var window = new Window { Width = 400, Height = 300, Content = view };
                window.Show();
                Settle();

                view.Text = "apiVersion: v1\n# keep me\nbogus: 1\nkind: Service\n";
                Settle();

                view.ApplyFix(new QuickFix("Remove 'bogus'", new TextEdit(2, 3, [])));

                Assert.Equal("apiVersion: v1\n# keep me\nkind: Service\n", view.Text.ReplaceLineEndings("\n"));
            },
            CancellationToken.None);
}
