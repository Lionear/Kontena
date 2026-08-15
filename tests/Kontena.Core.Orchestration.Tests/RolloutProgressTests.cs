using Kontena.Core.Orchestration.Provisioning;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class RolloutProgressTests
{
    private static RolloutTracker Tracker() => new([
        new RemoteClusterHost("10.10.4.11", ClusterHostRole.Controller),
        new RemoteClusterHost("10.10.4.21", ClusterHostRole.Worker),
    ]);

    private static RolloutHost Host(RolloutTracker tracker, string address) =>
        tracker.Hosts.Single(h => h.Address == address);

    [Fact]
    public void Every_machine_starts_waiting_at_the_first_step()
    {
        var tracker = Tracker();

        Assert.All(tracker.Hosts, h => Assert.Equal(RolloutHostState.Waiting, h.State));
        Assert.All(tracker.Hosts, h => Assert.Equal(RolloutStep.Connect, h.Step));
    }

    [Fact]
    public void A_line_naming_a_machine_moves_only_that_one()
    {
        var tracker = Tracker();
        tracker.Consume("✔ 10.10.4.11: connected");

        Assert.Equal(RolloutHostState.Running, Host(tracker, "10.10.4.11").State);
        Assert.Equal(RolloutHostState.Waiting, Host(tracker, "10.10.4.21").State);
    }

    [Fact]
    public void A_line_naming_nobody_moves_nobody()
    {
        var tracker = Tracker();
        tracker.Consume("⡿ Gathering k0s facts");

        Assert.All(tracker.Hosts, h => Assert.Equal(RolloutHostState.Waiting, h.State));
    }

    [Theory]
    [InlineData("✔ 10.10.4.11: connected", RolloutStep.Connect)]
    [InlineData("✔ 10.10.4.11: uploading k0s binary", RolloutStep.Binary)]
    [InlineData("✔ 10.10.4.11: installing k0s controller", RolloutStep.Join)]
    [InlineData("✔ 10.10.4.11: starting service", RolloutStep.Service)]
    [InlineData("✔ 10.10.4.11: node is ready", RolloutStep.Healthy)]
    public void The_step_is_read_off_the_tools_own_words(string line, RolloutStep expected)
    {
        var tracker = Tracker();
        tracker.Consume(line);

        Assert.Equal(expected, Host(tracker, "10.10.4.11").Step);
    }

    [Fact]
    public void A_machine_never_walks_backwards()
    {
        var tracker = Tracker();

        tracker.Consume("✔ 10.10.4.11: starting service");
        tracker.Consume("✔ 10.10.4.11: connected");

        // A summary line mentioning an earlier word must not make the row look like it came undone.
        Assert.Equal(RolloutStep.Service, Host(tracker, "10.10.4.11").Step);
    }

    [Fact]
    public void Reaching_healthy_is_done_without_waiting_for_the_run_to_end()
    {
        var tracker = Tracker();
        tracker.Consume("✔ 10.10.4.11: node is ready");

        Assert.Equal(RolloutHostState.Done, Host(tracker, "10.10.4.11").State);
    }

    [Fact]
    public void Trouble_beats_a_step_word_in_the_same_line()
    {
        var tracker = Tracker();
        tracker.Consume("✘ 10.10.4.21: failed to start service: exit status 1");

        var host = Host(tracker, "10.10.4.21");

        Assert.Equal(RolloutHostState.Failed, host.State);
        Assert.Contains("exit status 1", host.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_line_about_a_machine_is_kept_for_diagnosis()
    {
        var tracker = Tracker();

        tracker.Consume("✔ 10.10.4.11: connected");
        tracker.Consume("⡿ Gathering k0s facts");
        tracker.Consume("✔ 10.10.4.11: uploading k0s binary");

        // The tool already said what happened; diagnosis is finding it, not rewording it.
        Assert.Equal(2, Host(tracker, "10.10.4.11").Lines.Count);
        Assert.Empty(Host(tracker, "10.10.4.21").Lines);
    }

    [Fact]
    public void A_clean_finish_takes_the_tools_word_for_it()
    {
        var tracker = Tracker();
        tracker.Consume("✔ 10.10.4.11: installing k0s controller");
        tracker.Finish();

        // k0sctl exits zero only when the cluster is up, so the alternative is a row that says
        // "installing" forever because the last line used a word we do not know.
        Assert.All(tracker.Hosts, h => Assert.Equal(RolloutHostState.Done, h.State));
    }

    [Fact]
    public void A_failure_lands_on_whatever_was_mid_flight_and_leaves_the_untouched_alone()
    {
        var tracker = Tracker();
        tracker.Consume("✔ 10.10.4.11: installing k0s controller");
        tracker.Fail("host 10.10.4.11: exit status 1");

        Assert.Equal(RolloutHostState.Failed, Host(tracker, "10.10.4.11").State);

        // Never reached, so it stays waiting — saying otherwise would invent a fact.
        Assert.Equal(RolloutHostState.Waiting, Host(tracker, "10.10.4.21").State);
    }

    [Fact]
    public void A_failure_already_read_from_the_output_is_not_overwritten()
    {
        var tracker = Tracker();
        tracker.Consume("✘ 10.10.4.21: failed to start service");
        tracker.Fail("k0sctl exited 1");

        Assert.Contains("failed to start service", Host(tracker, "10.10.4.21").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Skipping_leaves_a_machine_out_without_calling_it_failed()
    {
        var tracker = Tracker();
        tracker.Consume("✘ 10.10.4.21: failed to start service");
        tracker.Skip("10.10.4.21");

        Assert.Equal(RolloutHostState.Skipped, Host(tracker, "10.10.4.21").State);
        Assert.Null(tracker.Stopped);
    }

    // ── Resume ───────────────────────────────────────────────────────────────

    [Fact]
    public void What_stood_at_the_end_is_what_a_resumed_run_can_skip()
    {
        var tracker = Tracker();
        tracker.Consume("✔ 10.10.4.11: node is ready");
        tracker.Consume("✘ 10.10.4.21: failed to start service");

        Assert.Equal(["10.10.4.11"], tracker.Standing.Select(h => h.Address));
        Assert.Equal("10.10.4.21", tracker.Stopped?.Address);
    }

    [Fact]
    public void Restoring_puts_back_what_an_earlier_run_finished()
    {
        var tracker = Tracker();
        tracker.Restore(["10.10.4.11"]);

        var restored = Host(tracker, "10.10.4.11");

        Assert.Equal(RolloutHostState.Done, restored.State);
        Assert.Equal(RolloutStep.Healthy, restored.Step);
        Assert.Contains("earlier run", restored.Detail, StringComparison.Ordinal);

        // The other one is genuinely not done, and a resume must not claim it is.
        Assert.Equal(RolloutHostState.Waiting, Host(tracker, "10.10.4.21").State);
    }

    [Fact]
    public void Restoring_an_address_that_is_not_in_this_spec_is_ignored()
    {
        var tracker = Tracker();
        tracker.Restore(["10.10.4.99"]);

        Assert.All(tracker.Hosts, h => Assert.Equal(RolloutHostState.Waiting, h.State));
    }

    [Fact]
    public void Restoring_only_ever_marks_a_machine_done_never_failed()
    {
        var tracker = Tracker();
        tracker.Restore(["10.10.4.11", "10.10.4.21"]);

        // A record of a previous failure is history; this run has not failed yet.
        Assert.Null(tracker.Stopped);
    }
}

public class RolloutRecordStoreTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"kontena-rollout-{Guid.NewGuid():N}")).FullName;

    private RolloutRecordStore Store() => new(Path.Combine(_dir, "rollout.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The operating system's problem, not the test's.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Nothing_written_means_nothing_to_resume()
    {
        Assert.Null(Store().Read());
    }

    [Fact]
    public void What_was_standing_survives_the_app_closing()
    {
        var store = Store();
        store.Write(new RolloutRecord("prod-eu-west", ["10.10.4.11", "10.10.4.12"], "10.10.4.21", DateTimeOffset.UtcNow));

        // A different instance, as the next launch would be.
        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal("prod-eu-west", read.ClusterName);
        Assert.Equal(["10.10.4.11", "10.10.4.12"], read.Standing);
        Assert.Equal("10.10.4.21", read.Stopped);
    }

    [Fact]
    public void A_record_of_nothing_is_not_offered()
    {
        Store().Write(new RolloutRecord("prod", [], null, DateTimeOffset.UtcNow));

        Assert.Null(Store().Read());
    }

    [Fact]
    public void A_finished_rollout_leaves_nothing_behind()
    {
        var store = Store();
        store.Write(new RolloutRecord("prod", ["10.10.4.11"], null, DateTimeOffset.UtcNow));
        store.Clear();

        Assert.Null(Store().Read());
    }

    [Fact]
    public void Clearing_something_that_is_not_there_is_not_an_error()
    {
        Store().Clear();
        Store().Clear();
    }

    [Fact]
    public void An_unreadable_record_is_no_record_rather_than_a_crash_on_startup()
    {
        File.WriteAllText(Path.Combine(_dir, "rollout.json"), "{ this is not json");

        Assert.Null(Store().Read());
    }
}
