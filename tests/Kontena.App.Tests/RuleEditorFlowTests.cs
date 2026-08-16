using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The rule editor end to end through the shell (KON-210): Alerts → New rule → Apply, and where that
/// last step lands. The claim being pinned is the one the ticket is built on — <b>there is no second
/// apply path</b> — and the only place it can be shown is here, where the routing is.
/// </summary>
public sealed class RuleEditorFlowTests
{
    private static async Task<MainWindowViewModel> EditorShellAsync()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.NavigateCommand.Execute("alerts");
        for (var i = 0; i < 100 && shell.CurrentPage is IListPage { HasLoaded: false }; i++)
            await Task.Delay(5);

        Assert.IsType<ClusterAlertsViewModel>(shell.CurrentPage).NewRuleCommand.Execute(null);
        return shell;
    }

    [Fact]
    public async Task New_rule_on_the_Alerts_page_opens_the_editor()
    {
        var shell = await EditorShellAsync();

        var editor = Assert.IsType<RuleEditorViewModel>(shell.CurrentPage);
        await editor.Loaded;

        // No Prometheus CR behind the fake, so the editor works and says what it could not read
        // rather than pretending the selectors are known.
        Assert.NotEmpty(editor.NamespaceOptions);
        Assert.NotEmpty(editor.SelectorNotice);
    }

    [Fact]
    public async Task Applying_a_rule_lands_on_the_apply_page_with_the_manifest_it_previewed()
    {
        var shell = await EditorShellAsync();
        var editor = Assert.IsType<RuleEditorViewModel>(shell.CurrentPage);
        await editor.Loaded;

        editor.AlertName = "AppHighErrorRate";
        editor.Expression = "up == 0";
        editor.ObjectName = "checkout-slo";
        editor.NamespaceName = "monitoring";

        var manifest = editor.Manifest;
        editor.ApplyCommand.Execute(null);

        // The ordinary apply page, with the dry-run still ahead of it: authored rules get reviewed
        // like everything else rather than reaching the cluster through a private button.
        var apply = Assert.IsType<ApplyManifestViewModel>(shell.CurrentPage);
        Assert.Equal(manifest, apply.YamlText);
        Assert.Equal("monitoring", apply.RenderNamespace);
        Assert.Equal(ManifestSourceKind.Paste, apply.SourceKind);
        Assert.False(apply.HasPlan);
        Assert.False(apply.CanApply);
    }
}
