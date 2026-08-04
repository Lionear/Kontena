using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.ManifestStudio.Tests.Git;

public sealed class GitCliTests
{
    // Matched by Executable alone (FakeToolRunner keys on that string) — does not need to be GitCli's
    // own private ExternalTool instance.
    private static readonly ExternalTool GitTool = new("git", "git", ["--version"], []);

    [Fact]
    public async Task Git_not_on_path_fails_clearly()
    {
        var cli = new GitCli(new FakeToolRunner());

        var result = await cli.StatusAsync("/repo");

        Assert.False(result.Ok);
        Assert.Contains("git", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_is_parsed_from_the_porcelain_output()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(i => i.Arguments[0] == "status", output: ["## main...origin/main [ahead 1]", " M deployment.yaml"]);
        var cli = new GitCli(runner);

        var result = await cli.StatusAsync("/repo");

        Assert.True(result.Ok);
        Assert.Equal("main", result.Status!.Branch);
        Assert.Equal(1, result.Status.Ahead);
        Assert.Single(result.Status.Changes);
    }

    [Fact]
    public async Task Commit_stages_everything_before_committing()
    {
        var runner = new FakeToolRunner().Install(GitTool);
        var cli = new GitCli(runner);

        await cli.CommitAsync("/repo", "add sample deployment");

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(["add", "-A"], runner.Invocations[0].Arguments);
        Assert.Equal(["commit", "-m", "add sample deployment"], runner.Invocations[1].Arguments);
    }

    [Fact]
    public async Task A_failed_add_never_attempts_the_commit()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(i => i.Arguments[0] == "add", errorOutput: ["fatal: not a git repository"], exitCode: 1);
        var cli = new GitCli(runner);

        var result = await cli.CommitAsync("/repo", "message");

        Assert.False(result.Ok);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Commit_and_push_stops_after_a_failed_commit()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(i => i.Arguments[0] == "commit", errorOutput: ["nothing to commit"], exitCode: 1);
        var cli = new GitCli(runner);

        var result = await cli.CommitAndPushAsync("/repo", "message");

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments[0] == "push");
    }

    [Fact]
    public async Task Commit_and_push_pushes_after_a_successful_commit()
    {
        var runner = new FakeToolRunner().Install(GitTool);
        var cli = new GitCli(runner);

        var result = await cli.CommitAndPushAsync("/repo", "message");

        Assert.True(result.Ok);
        Assert.Contains(runner.Invocations, i => i.Arguments[0] == "push");
    }

    [Fact]
    public async Task Branches_are_listed_from_the_ref_format()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(i => i.Arguments[0] == "branch", output: ["main", "feature/kon-295"]);
        var cli = new GitCli(runner);

        var branches = await cli.ListBranchesAsync("/repo");

        Assert.Equal(["main", "feature/kon-295"], branches);
    }
}
