using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.App.Tests;

public class RolloutViewModelTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"kontena-rollout-vm-{Guid.NewGuid():N}")).FullName;

    private RolloutRecordStore Records() => new(Path.Combine(_dir, "rollout.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Not the test's problem.
        }

        GC.SuppressFinalize(this);
    }

    private static RemoteClusterSpec Spec() =>
        new("prod-eu-west", [
            new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller),
            new RemoteClusterHost("10.10.4.21", ClusterHostRole.Worker),
        ]);

    private static readonly SshCredentials Login = new("rick");

    // ── Running ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_clean_rollout_ends_with_every_machine_up()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), Records());

        await vm.RunAsync(Spec(), Login);

        Assert.True(vm.IsDone);
        Assert.All(vm.Rows, r => Assert.True(r.IsDone));
        Assert.Equal("2 of 2 machines ready", vm.Progress);
    }

    [Fact]
    public async Task The_tools_own_output_is_kept_verbatim()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), Records());

        await vm.RunAsync(Spec(), Login);

        // Not our summary of it — the same call the local create screen made.
        Assert.NotEmpty(vm.Output);
        Assert.Contains(vm.Output, l => l.Contains("10.10.4.11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_per_machine_shows_its_role_and_its_steps()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), Records());

        await vm.RunAsync(Spec(), Login);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("controller", vm.Rows[0].Role);
        Assert.Equal("worker", vm.Rows[1].Role);
        Assert.Equal(5, vm.Rows[0].Steps.Count);
    }

    // ── Failing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rollout_that_stops_lands_on_the_failure_screen_with_the_tools_complaint()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, Records());

        await vm.RunAsync(Spec(), Login);

        Assert.True(vm.IsFailed);
        Assert.NotNull(vm.Error);

        // The lines before it still arrived: a partial rollout is what a failed one looks like.
        Assert.Equal(3, vm.Output.Count);
    }

    [Fact]
    public async Task The_failure_screen_says_that_nothing_was_undone()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, Records());

        await vm.RunAsync(Spec(), Login);

        // Without this sentence the missing rollback button reads as an omission rather than a choice.
        Assert.Contains("Nothing has been undone", RolloutViewModel.NoRollback, StringComparison.Ordinal);
        Assert.Contains("would also take out the machines", RolloutViewModel.NoRollback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_three_exits_say_what_the_machine_is_left_holding()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, Records());

        await vm.RunAsync(Spec(), Login);

        foreach (var exit in new[] { vm.RetryHost, vm.ContinueWithout, vm.Diagnose })
        {
            Assert.False(string.IsNullOrWhiteSpace(exit.Title));
            Assert.False(string.IsNullOrWhiteSpace(exit.Detail));

            // The line underneath. It is the question someone actually has at that moment.
            Assert.False(string.IsNullOrWhiteSpace(exit.LeftBehind));
        }
    }

    [Fact]
    public async Task Continuing_without_a_controller_says_what_that_does_to_the_quorum()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 2 }, Records());

        await vm.RunAsync(
            new RemoteClusterSpec("prod", [
                new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller),
                new RemoteClusterHost("10.10.4.12", ClusterHostRole.Controller),
                new RemoteClusterHost("10.10.4.13", ClusterHostRole.Controller),
            ]),
            Login);

        Assert.True(vm.IsFailed);
        Assert.Contains("controller", vm.ContinueWithout.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipping_the_stopped_machine_leaves_it_out_without_touching_it()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, Records());

        await vm.RunAsync(Spec(), Login);
        var stopped = vm.Stopped;

        vm.SkipStoppedCommand.Execute(null);

        Assert.NotNull(stopped);
        Assert.Null(vm.Stopped);
        Assert.Contains(vm.Rows, r => r.IsSkipped);
    }

    [Fact]
    public async Task Diagnosis_shows_the_machines_own_lines_rather_than_a_rewording()
    {
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, Records());

        await vm.RunAsync(Spec(), Login);

        Assert.NotEmpty(vm.StoppedLines);
        Assert.All(vm.StoppedLines, l => Assert.Contains(vm.Stopped!.Address, l, StringComparison.Ordinal));
    }

    // ── Closing and resuming ─────────────────────────────────────────────────

    [Fact]
    public async Task An_interrupted_rollout_is_written_down_so_the_next_launch_knows()
    {
        var records = Records();
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, records);

        await vm.RunAsync(Spec(), Login);

        var record = records.Read();

        Assert.NotNull(record);
        Assert.Equal("prod-eu-west", record.ClusterName);
    }

    [Fact]
    public async Task A_finished_rollout_leaves_nothing_to_resume()
    {
        var records = Records();
        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), records);

        await vm.RunAsync(Spec(), Login);

        Assert.Null(records.Read());
    }

    [Fact]
    public async Task A_next_launch_sees_the_interrupted_rollout()
    {
        var records = Records();
        await new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 3 }, records)
            .RunAsync(Spec(), Login);

        // A fresh view model, as the next launch would build.
        var next = new RolloutViewModel(new FakeRemoteClusterProvisioner(), records);

        Assert.True(next.HasInterrupted);
        Assert.Equal("prod-eu-west", next.Interrupted?.ClusterName);
    }

    [Fact]
    public async Task Resuming_shows_what_the_earlier_run_already_installed()
    {
        var records = Records();
        records.Write(new RolloutRecord("prod-eu-west", ["10.10.4.11"], "10.10.4.21", DateTimeOffset.UtcNow));

        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner { FailAfter = 1 }, records);
        await vm.RunAsync(Spec(), Login);

        // The machine the earlier run finished is not re-drawn from scratch — and, crucially, the
        // resumed run is the same k0sctl apply, which is what makes this safe rather than a special path.
        var restored = vm.Rows.Single(r => r.Address == "10.10.4.11");
        Assert.True(restored.IsDone);
    }

    [Fact]
    public async Task A_resumed_rollout_for_a_different_cluster_does_not_borrow_its_progress()
    {
        var records = Records();
        records.Write(new RolloutRecord("something-else", ["10.10.4.11"], null, DateTimeOffset.UtcNow));

        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), records);
        await vm.RunAsync(Spec(), Login);

        // It finished cleanly, so everything is done — but not because the other cluster's record said so.
        Assert.True(vm.IsDone);
    }

    [Fact]
    public void Declining_the_offer_forgets_it()
    {
        var records = Records();
        records.Write(new RolloutRecord("prod-eu-west", ["10.10.4.11"], null, DateTimeOffset.UtcNow));

        var vm = new RolloutViewModel(new FakeRemoteClusterProvisioner(), records);
        Assert.True(vm.HasInterrupted);

        vm.DiscardInterruptedCommand.Execute(null);

        Assert.False(vm.HasInterrupted);
        Assert.Null(records.Read());
    }

    [Fact]
    public void Nothing_is_running_so_there_is_nothing_to_warn_about_on_close()
    {
        Assert.Null(new RolloutViewModel(new FakeRemoteClusterProvisioner(), Records()).ClosingWarning);
    }

    [Fact]
    public async Task While_it_runs_closing_the_app_is_spelled_out_rather_than_left_implicit()
    {
        var provisioner = new FakeRemoteClusterProvisioner { LineDelay = TimeSpan.FromMilliseconds(30) };
        var vm = new RolloutViewModel(provisioner, Records());

        var run = vm.RunAsync(Spec(), Login);

        while (!vm.IsRunning)
            await Task.Delay(5);

        var warning = vm.ClosingWarning;

        Assert.NotNull(warning);
        Assert.Contains("stops k0sctl", warning, StringComparison.Ordinal);
        Assert.Contains("Nothing is undone", warning, StringComparison.Ordinal);

        vm.CancelCommand.Execute(null);
        await run;
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_undoes_nothing()
    {
        var provisioner = new FakeRemoteClusterProvisioner { LineDelay = TimeSpan.FromMilliseconds(30) };
        var vm = new RolloutViewModel(provisioner, Records());

        var run = vm.RunAsync(Spec(), Login);

        while (!vm.IsRunning)
            await Task.Delay(5);

        vm.CancelCommand.Execute(null);
        await run;

        Assert.True(vm.IsFailed);
    }
}
