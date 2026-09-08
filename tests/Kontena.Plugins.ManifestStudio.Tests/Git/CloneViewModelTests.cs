using Kontena.Plugins.ManifestStudio.Git;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.ManifestStudio.Tests.Git;

/// <summary>
/// Cloning a repository into a workspace (KON-436). No headless session needed: everything here is
/// decided before Avalonia gets involved, and the one part that needs a window — the folder picker —
/// is handed to this model as a plain string.
/// </summary>
public sealed class CloneViewModelTests : IDisposable
{
    // Matched by Executable alone, same as GitCliTests.
    private static readonly ExternalTool GitTool = new("git", "git", ["--version"], []);

    private readonly string _parent = Directory.CreateTempSubdirectory("manifest-studio-clone-tests-").FullName;

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    [Theory]
    [InlineData("https://github.com/your-org/platform-manifests.git", "platform-manifests")]
    [InlineData("https://github.com/your-org/platform-manifests", "platform-manifests")]
    [InlineData("https://github.com/your-org/platform-manifests/", "platform-manifests")]
    [InlineData("git@github.com:your-org/platform-manifests.git", "platform-manifests")]
    [InlineData("  https://github.com/your-org/platform-manifests.git  ", "platform-manifests")]
    public void The_repository_names_the_folder_it_lands_in(string url, string expected)
    {
        var model = new CloneViewModel(new GitCli(new FakeToolRunner())) { ParentFolder = _parent, Url = url };

        Assert.Equal(Path.Combine(_parent, expected), model.TargetPath);
    }

    /// <summary>Both halves are needed before there is anywhere to clone to, and neither is worth an
    /// error message: an empty form is not a mistake, it is a form nobody has filled in yet.</summary>
    [Fact]
    public void There_is_nothing_to_clone_until_there_is_a_url_and_a_folder()
    {
        var model = new CloneViewModel(new GitCli(new FakeToolRunner()));

        Assert.Null(model.TargetPath);
        Assert.False(model.CloneCommand.CanExecute(null));

        model.Url = "https://github.com/your-org/platform-manifests.git";
        Assert.False(model.CloneCommand.CanExecute(null));

        model.ParentFolder = _parent;
        Assert.True(model.CloneCommand.CanExecute(null));
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task A_clone_lands_in_the_chosen_folder_and_announces_it()
    {
        var runner = new FakeToolRunner().Install(GitTool);
        var model = new CloneViewModel(new GitCli(runner))
        {
            Url = "https://github.com/your-org/platform-manifests.git",
            ParentFolder = _parent,
        };

        string? announced = null;
        model.Cloned += (_, path) => announced = path;

        await model.CloneCommand.ExecuteAsync(null);

        var target = Path.Combine(_parent, "platform-manifests");
        Assert.Null(model.Error);
        Assert.Equal(target, announced);

        var invocation = Assert.Single(runner.Invocations, i => i.Arguments[0] == "clone");
        Assert.Equal(
            ["clone", "--progress", "--", "https://github.com/your-org/platform-manifests.git", target],
            invocation.Arguments);

        // The parent, not the target: the target is what git is about to create, and a working
        // directory that does not exist yet is a process that fails to start.
        Assert.Equal(_parent, invocation.WorkingDirectory);
    }

    /// <summary>
    /// A URL nobody can reach, a repository that is not there, a folder that already holds something —
    /// all one case here, because git is the one that tells them apart and its own sentence is what
    /// belongs on screen. Nothing gets opened on a failed clone.
    /// </summary>
    [Fact]
    public async Task A_clone_that_fails_reports_what_git_said_and_opens_nothing()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(
                i => i.Arguments[0] == "clone",
                errorOutput: ["fatal: destination path 'platform-manifests' already exists and is not an empty directory."],
                exitCode: 128);

        var model = new CloneViewModel(new GitCli(runner))
        {
            Url = "https://github.com/your-org/platform-manifests.git",
            ParentFolder = _parent,
        };

        var announced = false;
        model.Cloned += (_, _) => announced = true;

        await model.CloneCommand.ExecuteAsync(null);

        Assert.False(announced);
        Assert.Contains("already exists and is not an empty directory", model.Error);

        // Still usable afterwards: pick another folder and try again, without rebuilding the page.
        Assert.False(model.IsBusy);
        Assert.True(model.CloneCommand.CanExecute(null));
    }

    [Fact]
    public async Task Git_was_not_found_is_an_answer_rather_than_a_crash()
    {
        var model = new CloneViewModel(new GitCli(new FakeToolRunner()))
        {
            Url = "https://github.com/your-org/platform-manifests.git",
            ParentFolder = _parent,
        };

        await model.CloneCommand.ExecuteAsync(null);

        Assert.Contains("git", model.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The progress line is the whole reason cloning streams instead of buffering, so it has to be git's
    /// own words arriving while it runs — not a summary handed over once it is done. Watched through
    /// PropertyChanged because the command clears it again on the way out: a stale "83%" under a page
    /// that has finished is worse than nothing.
    /// </summary>
    [Fact]
    public async Task Gits_own_counting_is_what_shows_while_it_runs()
    {
        var runner = new FakeToolRunner()
            .Install(GitTool)
            .When(
                i => i.Arguments[0] == "clone",
                errorOutput: ["Cloning into 'platform-manifests'...", "Receiving objects: 100% (412/412), done."]);

        var model = new CloneViewModel(new GitCli(runner))
        {
            Url = "https://github.com/your-org/platform-manifests.git",
            ParentFolder = _parent,
        };

        var seen = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CloneViewModel.Progress) && model.Progress.Length > 0)
                seen.Add(model.Progress);
        };

        await model.CloneCommand.ExecuteAsync(null);

        Assert.Equal(["Cloning into 'platform-manifests'...", "Receiving objects: 100% (412/412), done."], seen);
        Assert.Equal(string.Empty, model.Progress);
    }
}
