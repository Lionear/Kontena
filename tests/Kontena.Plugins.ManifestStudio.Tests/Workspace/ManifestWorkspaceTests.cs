using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests.Workspace;

/// <summary>
/// Plan §2: the workspace is a fact read from the files on disk, not an import and not a naming
/// convention. These tests build a real temp folder rather than faking the file system, because the
/// behaviour under test — recursion, ordering, the <c>.git</c> exclusion — is exactly what a fake
/// would have to reimplement to be worth trusting.
/// </summary>
public sealed class ManifestWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path(params string[] parts) => parts.Aggregate(_root, System.IO.Path.Combine);

    [Fact]
    public void Opening_a_missing_folder_fails_clearly()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ManifestWorkspace.Open(Path("does-not-exist")));
    }

    [Fact]
    public void Folders_sort_before_files_and_git_plumbing_is_excluded()
    {
        Directory.CreateDirectory(Path(".git"));
        File.WriteAllText(Path(".git", "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path("base"));
        File.WriteAllText(Path("base", "deployment.yaml"), "kind: Deployment");
        File.WriteAllText(Path("README.md"), "# sample");

        var workspace = ManifestWorkspace.Open(_root);

        var names = workspace.Root.Children.Select(n => n.Name).ToArray();
        Assert.Equal(["base", "README.md"], names);

        var baseFolder = Assert.IsType<WorkspaceFolder>(workspace.Root.Children[0]);
        Assert.Equal(["deployment.yaml"], baseFolder.Children.Select(n => n.Name));
    }

    [Fact]
    public void A_kustomization_file_anywhere_under_root_marks_the_project()
    {
        Directory.CreateDirectory(Path("overlays", "prod"));
        File.WriteAllText(Path("overlays", "prod", "kustomization.yaml"), "resources: []");

        Assert.True(ManifestWorkspace.Open(_root).IsKustomizeProject);
    }

    [Fact]
    public void Plain_manifests_without_a_kustomization_file_are_not_a_kustomize_project()
    {
        File.WriteAllText(Path("deployment.yaml"), "kind: Deployment");

        Assert.False(ManifestWorkspace.Open(_root).IsKustomizeProject);
    }
}
