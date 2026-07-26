using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;

namespace Kontena.Core.Tests;

/// <summary>The tool catalogue and the install hints that go with it (KON-129).</summary>
public sealed class KnownToolsTests
{
    [Fact]
    public void Every_known_tool_can_say_how_to_install_it_somehow()
    {
        // Manual is the floor: kind and minikube are not in Debian's or Fedora's repositories, so on
        // those machines a link is the honest answer. Silence is not.
        foreach (var tool in KnownTools.All)
        {
            Assert.Contains(tool.InstallHints, h => h.Manager == PackageManager.Manual);
            Assert.False(string.IsNullOrWhiteSpace(tool.DocumentationUrl), $"{tool.Name} has no documentation link");
        }
    }

    [Fact]
    public void Install_hints_carry_an_argument_list_not_a_command_string()
    {
        // The whole seam avoids command strings; a hint that smuggled one in would be the one place a
        // shell could sneak back in when someone wires "run this for me" to it.
        foreach (var hint in KnownTools.All.SelectMany(t => t.InstallHints))
        {
            if (hint.Manager == PackageManager.Manual)
                continue;

            Assert.False(string.IsNullOrWhiteSpace(hint.Executable));
            Assert.NotEmpty(hint.Arguments);
            Assert.All(hint.Arguments, a => Assert.DoesNotContain(' ', a));
        }
    }

    [Fact]
    public void A_hint_renders_as_something_you_could_type()
    {
        var brew = KnownTools.Kind.InstallHints.First(h => h.Manager == PackageManager.Homebrew);

        Assert.Equal("brew install kind", brew.CommandLine);
    }

    [Fact]
    public void System_package_managers_declare_that_they_need_elevation()
    {
        // Knowing before the password prompt appears, rather than after.
        var dnf = KnownTools.Podman.InstallHints.First(h => h.Manager == PackageManager.Dnf);
        var brew = KnownTools.Podman.InstallHints.First(h => h.Manager == PackageManager.Homebrew);

        Assert.True(dnf.RequiresElevation);
        Assert.False(brew.RequiresElevation);
    }

    [Fact]
    public void Best_falls_back_to_manual_when_no_manager_is_present()
    {
        var tool = new ExternalTool("nothing", "nothing", [], [
            new InstallHint(PackageManager.Scoop, "scoop", ["install", "nothing"]),
            new InstallHint(PackageManager.Manual, "", []),
        ]);

        // Whatever this machine has, it does not have a manager that installs "nothing" — except on a
        // Windows box with scoop, which is the one case the first branch covers.
        var best = PackageManagers.Best(tool);

        Assert.NotNull(best);
        Assert.True(best.Manager is PackageManager.Manual or PackageManager.Scoop);
    }

    [Fact]
    public void Describe_quotes_only_what_needs_it()
    {
        var text = ToolCommand.Describe("kind", ["create", "cluster", "--name", "my cluster"]);

        Assert.Equal("kind create cluster --name \"my cluster\"", text);
    }

    [Fact]
    public async Task The_fake_reports_what_it_was_asked_to_run()
    {
        var runner = new FakeToolRunner().Install(KnownTools.Kind, "kind v0.30.0");

        var found = await runner.FindAsync(KnownTools.Kind);
        await runner.RunAsync(new ToolInvocation(KnownTools.Kind, ["create", "cluster", "--name", "dev"]));

        Assert.True(found.Found);
        Assert.Equal("kind v0.30.0", found.Version);
        Assert.Equal("kind create cluster --name dev", runner.Invocations.Single().CommandLine);
    }

    [Fact]
    public async Task The_fake_refuses_a_tool_that_was_not_installed()
    {
        var runner = new FakeToolRunner();

        await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await runner.RunAsync(new ToolInvocation(KnownTools.Minikube, ["start"])));
    }

    [Fact]
    public async Task The_fake_streams_scripted_output_and_fails_on_a_non_zero_exit()
    {
        var runner = new FakeToolRunner()
            .Install(KnownTools.Kind)
            .When(i => i.Arguments.Contains("create"), output: ["Creating cluster", "Ensuring node image"], exitCode: 1,
                errorOutput: ["ERROR: failed to create cluster"]);

        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<ToolFailedException>(async () =>
        {
            await foreach (var line in runner.StreamAsync(new ToolInvocation(KnownTools.Kind, ["create", "cluster"])))
                seen.Add(line.Text);
        });

        Assert.Equal(["Creating cluster", "Ensuring node image", "ERROR: failed to create cluster"], seen);
        Assert.Contains("failed to create cluster", ex.Complaint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fake_can_present_a_tool_that_is_there_but_broken()
    {
        var runner = new FakeToolRunner().InstallBroken(KnownTools.Kubectl);

        var found = await runner.FindAsync(KnownTools.Kubectl);

        Assert.True(found.Found);
        Assert.True(found.FoundButUnusable);
    }
}
