using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.ManifestStudio.Tests.Git;

public sealed class GitViewModelTests
{
    private static readonly ExternalTool GitTool = new("git", "git", ["--version"], []);

    private static GitViewModel Build(FakeToolRunner runner) => new(new GitCli(runner), "/repo");

    [Fact]
    public async Task Refresh_populates_status()
    {
        var runner = new FakeToolRunner().Install(GitTool).When(i => i.Arguments[0] == "status", output: ["## main"]);
        var vm = Build(runner);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Status);
        Assert.Equal("main", vm.Status!.Branch);
        Assert.Null(vm.Error);
    }

    [Fact]
    public void Commit_cannot_execute_with_an_empty_message()
    {
        var vm = Build(new FakeToolRunner().Install(GitTool));

        Assert.False(vm.CommitCommand.CanExecute(null));

        vm.CommitMessage = "add sample deployment";

        Assert.True(vm.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_successful_commit_clears_the_message_and_refreshes_status()
    {
        var runner = new FakeToolRunner().Install(GitTool).When(i => i.Arguments[0] == "status", output: ["## main"]);
        var vm = Build(runner);
        vm.CommitMessage = "add sample deployment";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.CommitMessage);
        Assert.Null(vm.Error);
        Assert.NotNull(vm.Status);
    }

    [Fact]
    public async Task A_failed_commit_keeps_the_message_and_reports_the_error()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(i => i.Arguments[0] == "commit", errorOutput: ["nothing to commit"], exitCode: 1);
        var vm = Build(runner);
        vm.CommitMessage = "add sample deployment";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal("add sample deployment", vm.CommitMessage);
        Assert.Equal("nothing to commit", vm.Error);
    }

    [Fact]
    public async Task Push_never_touches_the_commit_message()
    {
        var runner = new FakeToolRunner().Install(GitTool);
        var vm = Build(runner);
        vm.CommitMessage = "still typing this commit";

        await vm.PushCommand.ExecuteAsync(null);

        Assert.Equal("still typing this commit", vm.CommitMessage);
    }
}
