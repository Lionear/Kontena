namespace Kontena.App.Tests;

/// <summary>
/// Which panels are documents and which are consoles (KON-196).
/// <para>
/// <c>Console</c> stays dark in both themes because a terminal that turns white is a surprise, and the
/// ANSI colours written into it are not all readable on white. A manifest or a command preview is not
/// a terminal: it is text you read next to the rest of the page, and in light mode it was a black
/// plate with dark grey text on it — the panel was dark, the foreground token followed the theme, so
/// the YAML was very nearly invisible.
/// </para>
/// <para>
/// Pinned as a list because the mistake is not a value, it is a choice made per panel: the next YAML
/// surface will reach for <c>Console</c> like these five did. The contrast of the token itself is
/// covered by <c>PaletteContrastTests</c>, which now sweeps <c>CodeSurface</c> along with the others.
/// </para>
/// </summary>
public sealed class CodeSurfaceUsageTests
{
    /// <summary>Panels holding a document: a manifest, a diff-free preview, a command to copy.</summary>
    private static readonly string[] Documents =
        [
            "ObjectYamlView.axaml",          // read-only YAML for every cluster kind
            "ClusterPodDetailView.axaml",    // the editable manifest (its log list stays a console)
            "LocalClustersView.axaml",       // kind/minikube command + config preview
            "ClusterToolingView.axaml",      // the install hint command
        ];

    /// <summary>Panels holding a stream: a terminal, or output as it arrives.</summary>
    private static readonly string[] Consoles =
        [
            "TerminalView.axaml",
            "ComposeLogsView.axaml",
            "ComposeUpView.axaml",
            "BuildImageView.axaml",
            "ContainerDetailView.axaml",
        ];

    private static string Read(string view) =>
        File.ReadAllText(Path.Combine(ViewsDirectory(), view));

    [Theory]
    [MemberData(nameof(DocumentViews))]
    public void A_document_panel_uses_the_code_surface(string view) =>
        Assert.Contains("DynamicResource CodeSurface", Read(view), StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(ConsoleViews))]
    public void A_console_panel_stays_on_the_console(string view)
    {
        var xaml = Read(view);

        Assert.Contains("DynamicResource Console", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource CodeSurface", xaml, StringComparison.Ordinal);
    }

    public static TheoryData<string> DocumentViews() => [.. Documents];

    public static TheoryData<string> ConsoleViews() => [.. Consoles];

    /// <summary>Walks up to the repository root, the way <c>PaletteContrastTests</c> finds App.axaml.</summary>
    private static string ViewsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var views = Path.Combine(dir.FullName, "src", "Kontena.App", "Views");
            if (Directory.Exists(views))
                return views;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/Kontena.App/Views from the test output directory.");
    }
}
