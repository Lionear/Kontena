using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests.Workspace;

public sealed class WorkspaceViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-vm-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteFile(string name, string content = "kind: Deployment\n")
    {
        var path = System.IO.Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private WorkspaceViewModel Build() => new(ManifestWorkspace.Open(_root));

    [Fact]
    public void Opening_a_file_adds_a_tab_and_activates_it()
    {
        var path = WriteFile("a.yaml");
        var vm = Build();

        var document = vm.Open(path);

        Assert.Same(document, Assert.Single(vm.OpenTabs));
        Assert.Same(document, vm.ActiveDocument);
    }

    [Fact]
    public void Opening_the_same_path_twice_reuses_the_tab()
    {
        var path = WriteFile("a.yaml");
        var vm = Build();

        var first = vm.Open(path);
        var second = vm.Open(path);

        Assert.Same(first, second);
        Assert.Single(vm.OpenTabs);
    }

    [Fact]
    public void Closing_the_active_tab_activates_its_new_neighbour()
    {
        var vm = Build();
        var a = vm.Open(WriteFile("a.yaml"));
        var b = vm.Open(WriteFile("b.yaml"));
        var c = vm.Open(WriteFile("c.yaml"));
        vm.ActiveDocument = b;

        vm.Close(b);

        Assert.Equal([a, c], vm.OpenTabs);
        Assert.Same(c, vm.ActiveDocument);
    }

    [Fact]
    public void Closing_an_inactive_tab_leaves_the_active_one_alone()
    {
        var vm = Build();
        var a = vm.Open(WriteFile("a.yaml"));
        var b = vm.Open(WriteFile("b.yaml"));
        vm.ActiveDocument = b;

        vm.Close(a);

        Assert.Same(b, vm.ActiveDocument);
    }

    [Fact]
    public void Closing_the_last_tab_leaves_nothing_active()
    {
        var vm = Build();
        var only = vm.Open(WriteFile("a.yaml"));

        vm.Close(only);

        Assert.Empty(vm.OpenTabs);
        Assert.Null(vm.ActiveDocument);
    }

    [Fact]
    public void Save_active_command_is_only_enabled_with_an_active_document()
    {
        var vm = Build();
        Assert.False(vm.SaveActiveCommand.CanExecute(null));

        var path = WriteFile("a.yaml");
        var document = vm.Open(path);
        Assert.True(vm.SaveActiveCommand.CanExecute(null));

        document.Text = "kind: StatefulSet\n";
        vm.SaveActiveCommand.Execute(null);

        Assert.False(document.IsDirty);
        Assert.Equal("kind: StatefulSet\n", File.ReadAllText(path));
    }
}
