using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class HostPreflightTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock that does not move. Four lines beats a package reference for one fixed instant.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static RemoteClusterHost Controller(string address = "10.0.0.1") =>
        new(address, ClusterHostRole.Controller);

    private static RemoteClusterHost Worker(string address = "10.0.0.2") =>
        new(address, ClusterHostRole.Worker);

    /// <summary>A machine where everything is as it should be. Tests spoil one answer at a time.</summary>
    private static FakePreflightProbe Healthy(string address, string arch = "x86_64", string? hostname = null) =>
        new FakePreflightProbe(address)
            .Answer("echo kontena-preflight", ProbeResult.Success("kontena-preflight"))
            .Answer("sudo -n true", ProbeResult.Success())
            .Answer("uname", ProbeResult.Success($"Linux {arch}"))
            .Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 0.0.0.0:22 0.0.0.0:*"))
            .Answer("swapon", ProbeResult.Success())
            .Answer("date +%s", ProbeResult.Success(Now.ToUnixTimeSeconds().ToString()))
            .Answer("hostname", ProbeResult.Success(
                $"{hostname ?? address.Replace('.', '-')}\n{Guid.NewGuid()}\naa:bb:cc:{address[^1]}{address[^1]}:00:01,"));

    private static Task<PreflightReport> RunAsync(
        IReadOnlyList<(RemoteClusterHost Host, FakePreflightProbe Probe)> machines, string? cni = null)
    {
        return HostPreflight.RunAsync(
            [.. machines.Select(m => m.Host)],
            host => machines.First(m => m.Host.Address == host.Address).Probe,
            cni,
            new FixedTime(Now));
    }

    private static PreflightFinding Finding(PreflightReport report, PreflightCheck check, string target) =>
        Assert.Single(report.Findings.Where(f => f.Check == check && f.Target == target));

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_healthy_pair_of_machines_passes_and_lets_the_run_continue()
    {
        var report = await RunAsync([
            (Controller(), Healthy("10.0.0.1")),
            (Worker(), Healthy("10.0.0.2")),
        ]);

        Assert.True(report.CanContinue);
        Assert.Empty(report.Blockers);
        Assert.Empty(report.Warnings);
        Assert.Equal("Every machine is ready.", report.Summary);
    }

    [Fact]
    public async Task Every_finding_carries_a_reason_whatever_the_outcome()
    {
        var report = await RunAsync([(Controller(), Healthy("10.0.0.1").Unreachable())]);

        Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
    }

    // ── Reachability, and what it takes down with it ─────────────────────────

    [Fact]
    public async Task An_unreachable_machine_fails_reachability_and_leaves_the_rest_unknown()
    {
        var report = await RunAsync([(Controller(), Healthy("10.0.0.1").Unreachable("No route to host."))]);

        Assert.Equal(PreflightOutcome.Failed, Finding(report, HostPreflight.Reachable, "10.0.0.1").Outcome);
        Assert.Contains("No route to host.", Finding(report, HostPreflight.Reachable, "10.0.0.1").Reason, StringComparison.Ordinal);

        // Not "swap is off" — we never looked, and saying either way would be inventing the fact.
        foreach (var check in new[] { HostPreflight.Sudo, HostPreflight.Swap, HostPreflight.Ports, HostPreflight.Clock })
            Assert.Equal(PreflightOutcome.Unknown, Finding(report, check, "10.0.0.1").Outcome);

        Assert.False(report.CanContinue);
    }

    [Fact]
    public async Task A_machine_that_connects_but_has_no_usable_shell_is_not_reachable_either()
    {
        var probe = Healthy("10.0.0.1").Answer("echo kontena-preflight", ProbeResult.Exit(126));

        var report = await RunAsync([(Controller(), probe)]);

        Assert.Equal(PreflightOutcome.Failed, Finding(report, HostPreflight.Reachable, "10.0.0.1").Outcome);
        Assert.False(report.CanContinue);
    }

    // ── One check at a time ──────────────────────────────────────────────────

    [Fact]
    public async Task Sudo_that_wants_a_password_blocks_and_says_why_a_prompt_is_fatal()
    {
        var probe = Healthy("10.0.0.1").Answer("sudo -n true", ProbeResult.Exit(1));

        var finding = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Sudo, "10.0.0.1");

        Assert.Equal(PreflightOutcome.Failed, finding.Outcome);
        Assert.Contains("hang", finding.Reason, StringComparison.Ordinal);
        Assert.True(finding.Blocks);
    }

    [Fact]
    public async Task Swap_that_is_on_blocks_and_offers_the_one_command_that_fixes_it()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon", ProbeResult.Success("/dev/dm-1"));

        var finding = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Swap, "10.0.0.1");

        Assert.Equal(PreflightOutcome.Failed, finding.Outcome);
        Assert.Contains("kubelet", finding.Reason, StringComparison.Ordinal);
        Assert.NotNull(finding.Remedy);
        Assert.Contains("swapoff -a", finding.Remedy.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clock_drift_warns_and_offers_nothing_because_the_fix_is_not_ours_to_choose()
    {
        var probe = Healthy("10.0.0.1")
            .Answer("date +%s", ProbeResult.Success(Now.AddMinutes(4).ToUnixTimeSeconds().ToString()));

        var finding = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Clock, "10.0.0.1");

        Assert.Equal(PreflightOutcome.Warned, finding.Outcome);
        Assert.Contains("etcd", finding.Reason, StringComparison.Ordinal);

        // No remedy: setting a clock means choosing a time source, which belongs to whoever runs it.
        Assert.Null(finding.Remedy);
    }

    [Fact]
    public async Task A_clock_a_second_out_is_not_worth_mentioning()
    {
        var probe = Healthy("10.0.0.1")
            .Answer("date +%s", ProbeResult.Success(Now.AddSeconds(1).ToUnixTimeSeconds().ToString()));

        Assert.Equal(
            PreflightOutcome.Passed,
            Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Clock, "10.0.0.1").Outcome);
    }

    [Fact]
    public async Task Drift_in_either_direction_counts()
    {
        var probe = Healthy("10.0.0.1")
            .Answer("date +%s", ProbeResult.Success(Now.AddMinutes(-4).ToUnixTimeSeconds().ToString()));

        Assert.Equal(
            PreflightOutcome.Warned,
            Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Clock, "10.0.0.1").Outcome);
    }

    [Fact]
    public async Task A_busy_port_blocks_and_names_which_one_and_what_wants_it()
    {
        var probe = Healthy("10.0.0.1")
            .Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 *:6443 *:*"));

        var finding = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Ports, "10.0.0.1");

        Assert.Equal(PreflightOutcome.Failed, finding.Outcome);
        Assert.Contains("6443", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("kube-apiserver", finding.Reason, StringComparison.Ordinal);

        // Nothing is offered: what holds a port is another program, and stopping it unasked is a guess.
        Assert.Null(finding.Remedy);
    }

    [Fact]
    public async Task A_worker_is_not_asked_about_the_control_plane_ports()
    {
        var probe = Healthy("10.0.0.2")
            .Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 0.0.0.0:6443 0.0.0.0:*"));

        Assert.Equal(
            PreflightOutcome.Passed,
            Finding(await RunAsync([(Worker(), probe)]), HostPreflight.Ports, "10.0.0.2").Outcome);
    }

    [Fact]
    public async Task Calico_adds_bgp_to_the_ports_that_have_to_be_free()
    {
        var probe = Healthy("10.0.0.2").Answer("ss -Hltn", ProbeResult.Success("LISTEN 0 128 0.0.0.0:179 0.0.0.0:*"));

        var without = Finding(await RunAsync([(Worker(), probe)]), HostPreflight.Ports, "10.0.0.2");
        var with = Finding(await RunAsync([(Worker(), probe)], cni: "calico"), HostPreflight.Ports, "10.0.0.2");

        Assert.Equal(PreflightOutcome.Passed, without.Outcome);
        Assert.Equal(PreflightOutcome.Failed, with.Outcome);
        Assert.Contains("179", with.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_that_is_not_linux_is_a_warning_rather_than_a_refusal()
    {
        var probe = Healthy("10.0.0.1").Answer("uname", ProbeResult.Success("FreeBSD amd64"));

        var report = await RunAsync([(Controller(), probe)]);

        Assert.Equal(PreflightOutcome.Warned, Finding(report, HostPreflight.Platform, "10.0.0.1").Outcome);
        Assert.True(report.CanContinue);
    }

    // ── "Could not be checked" is its own answer ─────────────────────────────

    [Fact]
    public async Task A_missing_ss_leaves_the_ports_unknown_rather_than_claiming_they_are_free()
    {
        var probe = Healthy("10.0.0.1").Answer("ss -Hltn", ProbeResult.Exit(127));

        var finding = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Ports, "10.0.0.1");

        Assert.Equal(PreflightOutcome.Unknown, finding.Outcome);
        Assert.Contains("iproute2", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unverified_blocking_check_stops_the_run_as_firmly_as_a_failed_one()
    {
        var probe = Healthy("10.0.0.1").Answer("ss -Hltn", ProbeResult.Exit(127));

        var report = await RunAsync([(Controller(), probe)]);

        Assert.False(report.CanContinue);
        Assert.Single(report.Blockers);
    }

    [Fact]
    public async Task An_unverified_non_blocking_check_does_not_stop_anything()
    {
        var probe = Healthy("10.0.0.1").Answer("date +%s", ProbeResult.Exit(127));

        var report = await RunAsync([(Controller(), probe)]);

        Assert.Equal(PreflightOutcome.Unknown, Finding(report, HostPreflight.Clock, "10.0.0.1").Outcome);
        Assert.True(report.CanContinue);
    }

    [Fact]
    public async Task A_clock_that_answers_nonsense_is_unknown_rather_than_parsed_into_a_number()
    {
        var probe = Healthy("10.0.0.1").Answer("date +%s", ProbeResult.Success("tomorrow"));

        Assert.Equal(
            PreflightOutcome.Unknown,
            Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Clock, "10.0.0.1").Outcome);
    }

    // ── Across hosts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_machines_with_the_same_hostname_are_caught_as_the_clone_they_are()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1", hostname: "node")),
            (Worker("10.0.0.2"), Healthy("10.0.0.2", hostname: "node")),
        ]);

        var finding = Assert.Single(report.Findings.Where(f => f.Target == "cluster" && f.Check == HostPreflight.Identity));

        Assert.Equal(PreflightOutcome.Failed, finding.Outcome);
        Assert.Contains("cloning a VM", finding.Reason, StringComparison.Ordinal);
        Assert.False(report.CanContinue);
    }

    [Fact]
    public async Task A_shared_product_uuid_is_caught_even_when_the_hostnames_differ()
    {
        var uuid = Guid.NewGuid().ToString();

        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1").Answer("hostname", ProbeResult.Success($"cp-1\n{uuid}\naa:bb:cc:00:00:01,"))),
            (Worker("10.0.0.2"), Healthy("10.0.0.2").Answer("hostname", ProbeResult.Success($"w-1\n{uuid}\naa:bb:cc:00:00:02,"))),
        ]);

        var finding = Assert.Single(report.Findings.Where(f => f.Target == "cluster" && f.Check == HostPreflight.Identity));

        Assert.Contains("product_uuid", finding.Reason, StringComparison.Ordinal);
        Assert.False(report.CanContinue);
    }

    [Fact]
    public async Task A_shared_mac_address_is_caught_when_hostname_and_uuid_are_both_fine()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1").Answer("hostname", ProbeResult.Success($"cp-1\n{Guid.NewGuid()}\naa:bb:cc:00:00:09,"))),
            (Worker("10.0.0.2"), Healthy("10.0.0.2").Answer("hostname", ProbeResult.Success($"w-1\n{Guid.NewGuid()}\naa:bb:cc:00:00:09,"))),
        ]);

        var finding = Assert.Single(report.Findings.Where(f => f.Target == "cluster" && f.Check == HostPreflight.Identity));

        Assert.Contains("MAC address", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Distinct_machines_raise_nothing_across_the_cluster()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1")),
            (Worker("10.0.0.2"), Healthy("10.0.0.2")),
        ]);

        Assert.DoesNotContain(report.Findings, f => f.Target == "cluster");
    }

    [Fact]
    public async Task A_mix_of_architectures_warns_once_about_the_cluster_and_not_per_machine()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1", arch: "x86_64")),
            (Worker("10.0.0.2"), Healthy("10.0.0.2", arch: "aarch64")),
            (Worker("10.0.0.3"), Healthy("10.0.0.3", arch: "aarch64")),
        ]);

        var finding = Assert.Single(report.Findings.Where(f => f.Check == HostPreflight.Architecture));

        Assert.Equal(PreflightOutcome.Warned, finding.Outcome);
        Assert.Equal("cluster", finding.Target);
        Assert.Contains("multi-arch", finding.Reason, StringComparison.Ordinal);

        // Works, so it must not stop anything.
        Assert.True(report.CanContinue);
    }

    [Fact]
    public async Task One_architecture_across_the_fleet_says_nothing_at_all()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1", arch: "x86_64")),
            (Worker("10.0.0.2"), Healthy("10.0.0.2", arch: "x86_64")),
        ]);

        Assert.DoesNotContain(report.Findings, f => f.Check == HostPreflight.Architecture);
    }

    // ── The report ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Hosts_are_checked_at_the_same_time_rather_than_one_after_another()
    {
        var machines = Enumerable.Range(1, 6)
            .Select(i => (Host: Worker($"10.0.0.{i}"), Probe: Healthy($"10.0.0.{i}")))
            .ToList();

        await RunAsync(machines);

        // Every machine really was asked; the parallelism itself is Task.WhenAll's business.
        Assert.All(machines, m => Assert.Contains("swapon", string.Join('\n', m.Probe.Commands), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_summary_names_what_is_wrong_rather_than_only_counting_it()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon", ProbeResult.Success("/dev/dm-1"));

        var report = await RunAsync([(Controller(), probe)]);

        Assert.Contains("Swap is off", report.Summary, StringComparison.Ordinal);
        Assert.Contains("10.0.0.1", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_can_be_read_back_per_machine_for_the_table()
    {
        var report = await RunAsync([
            (Controller("10.0.0.1"), Healthy("10.0.0.1")),
            (Worker("10.0.0.2"), Healthy("10.0.0.2")),
        ]);

        Assert.All(report.For("10.0.0.1"), f => Assert.Equal("10.0.0.1", f.Target));
        Assert.Equal(7, report.For("10.0.0.1").Count);
    }

    // ── Applying a remedy ────────────────────────────────────────────────────

    [Fact]
    public async Task Turning_swap_off_runs_the_command_and_then_checks_again()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon --noheadings", ProbeResult.Success("/dev/dm-1"));
        var before = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Swap, "10.0.0.1");

        // The machine now reports no swap, as it would after swapoff.
        probe.Answer("swapon --noheadings", ProbeResult.Success());

        var after = await HostPreflight.ApplyAsync(before, probe);

        Assert.Contains("swapoff -a", string.Join('\n', probe.Commands), StringComparison.Ordinal);
        Assert.Equal(PreflightOutcome.Passed, after.Outcome);
    }

    [Fact]
    public async Task A_remedy_that_did_not_take_leaves_the_finding_failed()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon --noheadings", ProbeResult.Success("/dev/dm-1"));
        var before = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Swap, "10.0.0.1");

        // swapoff said yes; swap is still there. The re-check is what catches it.
        probe.Answer("swapoff -a", ProbeResult.Success());

        var after = await HostPreflight.ApplyAsync(before, probe);

        Assert.Equal(PreflightOutcome.Failed, after.Outcome);
    }

    [Fact]
    public async Task A_remedy_that_refused_says_so_without_losing_the_original_reason()
    {
        var probe = Healthy("10.0.0.1").Answer("swapon --noheadings", ProbeResult.Success("/dev/dm-1"));
        var before = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Swap, "10.0.0.1");

        probe.Answer("swapoff -a", ProbeResult.Exit(1));

        var after = await HostPreflight.ApplyAsync(before, probe);

        Assert.Equal(PreflightOutcome.Failed, after.Outcome);
        Assert.Contains("Turn swap off failed", after.Reason, StringComparison.Ordinal);
        Assert.Contains("kubelet", after.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finding_with_nothing_to_offer_comes_back_untouched()
    {
        var probe = Healthy("10.0.0.1")
            .Answer("date +%s", ProbeResult.Success(Now.AddMinutes(4).ToUnixTimeSeconds().ToString()));

        var drift = Finding(await RunAsync([(Controller(), probe)]), HostPreflight.Clock, "10.0.0.1");

        Assert.Same(drift, await HostPreflight.ApplyAsync(drift, probe));
    }

    // ── The bits worth testing on their own ──────────────────────────────────

    [Theory]
    [InlineData("LISTEN 0 4096 0.0.0.0:22 0.0.0.0:*", 22)]
    [InlineData("LISTEN 0 4096 [::]:6443 [::]:*", 6443)]
    [InlineData("LISTEN 0 4096 *:10250 *:*", 10250)]
    [InlineData("LISTEN 0 4096 127.0.0.53%lo:53 0.0.0.0:*", 53)]
    public void The_listening_ports_are_read_out_of_every_address_form(string line, int expected)
    {
        Assert.Contains(expected, HostPreflight.Listening(line));
    }

    [Fact]
    public void Rubbish_from_ss_yields_no_ports_rather_than_an_exception()
    {
        Assert.Empty(HostPreflight.Listening("what even is this"));
    }

    [Fact]
    public void A_controller_is_asked_about_etcd_and_the_api_server_and_a_worker_is_not()
    {
        var controller = HostPreflight.Required(new RemoteClusterHost("a", ClusterHostRole.Controller), null);
        var worker = HostPreflight.Required(new RemoteClusterHost("b", ClusterHostRole.Worker), null);

        Assert.Equal([6443, 10250, 2379, 2380], controller.Select(p => p.Port));
        Assert.Equal([10250], worker.Select(p => p.Port));
    }
}
