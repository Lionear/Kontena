using System.Globalization;
using Kontena.Sdk.Orchestration.Preflight;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Core.Orchestration.Preflight;

/// <summary>
/// Whether these machines can actually take a Kubernetes install, checked before anything is written
/// to any of them (KON-235).
/// <para>
/// Its own step rather than a box to tick, because what goes wrong here otherwise goes wrong halfway
/// through the rollout — and then there is a half-built cluster sitting on the machines, which is
/// worse than not starting.
/// </para>
/// <para>
/// Two passes. Per host in parallel, because eight machines answering one at a time is eight round
/// trips of waiting for no reason; then across hosts, for the two questions no single machine can
/// answer about itself — whether its identity is unique, and whether the fleet is of one architecture.
/// </para>
/// </summary>
public static class HostPreflight
{
    // ── The checks, as values, so a test and a screen name the same thing ────

    public static readonly PreflightCheck Reachable = new("reachable", "Reachable", true);
    public static readonly PreflightCheck Sudo = new("sudo", "Sudo without a password prompt", true);
    public static readonly PreflightCheck Platform = new("platform", "Operating system and architecture", false);
    public static readonly PreflightCheck Ports = new("ports", "Required ports are free", true);
    public static readonly PreflightCheck Swap = new("swap", "Swap is off", true);
    public static readonly PreflightCheck Clock = new("clock", "Clock is close to this machine's", false);
    public static readonly PreflightCheck Identity = new("identity", "Hostname, MAC and product_uuid are unique", true);
    public static readonly PreflightCheck Architecture = new("mixed-arch", "One architecture across the cluster", false);

    /// <summary>Every check this runs, in the order a page should list them.</summary>
    public static IReadOnlyList<PreflightCheck> All { get; } =
        [Reachable, Sudo, Platform, Ports, Swap, Clock, Identity, Architecture];

    /// <summary>How far the clocks may drift before it is worth saying. Certificates and etcd are
    /// unforgiving about this, and a few seconds is already worth knowing.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs every check over every host and reduces it to one report.
    /// </summary>
    /// <param name="hosts">The machines, with their roles — controllers are asked about more ports.</param>
    /// <param name="probeFor">How to reach one host. Injected so this runs against a fake in tests.</param>
    /// <param name="cni">Which CNI is planned, or null. Calico adds BGP on 179.</param>
    /// <param name="time">Clock to compare against, for the drift check.</param>
    public static async Task<PreflightReport> RunAsync(
        IReadOnlyList<RemoteClusterHost> hosts,
        Func<RemoteClusterHost, IPreflightProbe> probeFor,
        string? cni = null,
        TimeProvider? time = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(probeFor);

        var clock = time ?? TimeProvider.System;

        var perHost = await Task.WhenAll(
            hosts.Select(host => OneHostAsync(host, probeFor(host), cni, clock, ct)));

        var findings = perHost.SelectMany(r => r.Findings).ToList();
        findings.AddRange(AcrossHosts(perHost));

        return new PreflightReport(findings);
    }

    /// <summary>
    /// Runs a finding's remedy and checks again, returning the new finding.
    /// <para>
    /// Re-checks rather than assuming: a remedy that exited zero has still only been reported by the
    /// thing we asked to fix itself, and the whole point of a preflight is not to take that on trust.
    /// A finding with no remedy comes back untouched, so a caller can offer this on every row without
    /// deciding first which rows have one.
    /// </para>
    /// </summary>
    public static async Task<PreflightFinding> ApplyAsync(
        PreflightFinding finding, IPreflightProbe probe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(probe);

        if (finding.Remedy is not { } remedy)
            return finding;

        var result = await probe.RunAsync(remedy.Command, ct);

        if (!result.Ran)
            return PreflightFinding.Unknown(finding.Check, finding.Target, result.Failure ?? "The machine stopped answering.");

        if (!result.Ok)
        {
            return finding with
            {
                Reason = $"{remedy.Title} failed (exit {result.ExitCode}). {finding.Reason}",
            };
        }

        return await RecheckAsync(finding.Check, probe, finding.Target, ct);
    }

    /// <summary>
    /// Runs one check again on its own. Only the checks that have a remedy can get here; the switch
    /// grows with them rather than there being a registry for the one entry it would hold today.
    /// </summary>
    private static async Task<PreflightFinding> RecheckAsync(
        PreflightCheck check, IPreflightProbe probe, string target, CancellationToken ct) =>
        check.Id == Swap.Id
            ? await SwapAsync(probe, target, ct)
            : PreflightFinding.Unknown(check, target, "Fixed, but this check cannot confirm itself. Run the preflight again.");

    /// <summary>What one host answered, plus the facts the cross-host checks need from it.</summary>
    private sealed record HostPass(
        string Target,
        List<PreflightFinding> Findings,
        string? Architecture,
        string? Hostname,
        string? Uuid,
        IReadOnlyList<string> Macs);

    private static async Task<HostPass> OneHostAsync(
        RemoteClusterHost host, IPreflightProbe probe, string? cni, TimeProvider clock, CancellationToken ct)
    {
        var target = probe.Target;
        var findings = new List<PreflightFinding>();

        // Reachability first, and everything else depends on it. A machine we never reached has no
        // swap state to report — saying it has none would be inventing the very fact we came for.
        var hello = await probe.RunAsync("echo kontena-preflight", ct);

        if (!hello.Ran)
        {
            findings.Add(PreflightFinding.Fail(Reachable, target, hello.Failure ?? "Could not connect."));

            foreach (var check in new[] { Sudo, Platform, Ports, Swap, Clock, Identity })
                findings.Add(PreflightFinding.Unknown(check, target, "Not checked — the machine could not be reached."));

            return new HostPass(target, findings, null, null, null, []);
        }

        if (!hello.Ok)
        {
            findings.Add(PreflightFinding.Fail(
                Reachable, target,
                $"Connected, but a plain command exited {hello.ExitCode}. The login shell is refusing to run things."));

            foreach (var check in new[] { Sudo, Platform, Ports, Swap, Clock, Identity })
                findings.Add(PreflightFinding.Unknown(check, target, "Not checked — no usable shell on the machine."));

            return new HostPass(target, findings, null, null, null, []);
        }

        findings.Add(PreflightFinding.Pass(Reachable, target, "Answered a test command."));

        findings.Add(await SudoAsync(probe, target, ct));

        var (platform, architecture) = await PlatformAsync(probe, target, ct);
        findings.Add(platform);

        findings.Add(await PortsAsync(probe, target, host, cni, ct));
        findings.Add(await SwapAsync(probe, target, ct));
        findings.Add(await ClockAsync(probe, target, clock, ct));

        var (identity, hostname, uuid, macs) = await IdentityAsync(probe, target, ct);
        findings.Add(identity);

        return new HostPass(target, findings, architecture, hostname, uuid, macs);
    }

    // ── Per-host checks ──────────────────────────────────────────────────────

    private static async Task<PreflightFinding> SudoAsync(IPreflightProbe probe, string target, CancellationToken ct)
    {
        // -n makes sudo fail rather than prompt. A prompt is the failure mode that matters: the rollout
        // is not a terminal anyone is sitting at, so a password request is a hang, not a question.
        var result = await probe.RunAsync("sudo -n true", ct);

        if (!result.Ran)
            return PreflightFinding.Unknown(Sudo, target, result.Failure ?? "The check could not be run.");

        return result.Ok
            ? PreflightFinding.Pass(Sudo, target, "sudo runs without asking for a password.")
            : PreflightFinding.Fail(
                Sudo, target,
                "sudo wants a password. The rollout is not run from a terminal, so a prompt would hang "
                + "rather than ask. Give this user NOPASSWD sudo, or log in as root.");
    }

    private static async Task<(PreflightFinding Finding, string? Architecture)> PlatformAsync(
        IPreflightProbe probe, string target, CancellationToken ct)
    {
        var result = await probe.RunAsync("uname -s -m", ct);

        if (!result.Ran || !result.Ok)
        {
            return (PreflightFinding.Unknown(
                Platform, target, result.Failure ?? "uname would not answer, so the platform is unknown."), null);
        }

        var parts = result.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kernel = parts.Length > 0 ? parts[0] : "unknown";
        var architecture = parts.Length > 1 ? parts[1] : null;

        // Non-blocking on purpose: Kontena is not the authority on which distributions work, and
        // refusing to continue on an unrecognised one would be an opinion dressed as a check.
        return !string.Equals(kernel, "Linux", StringComparison.OrdinalIgnoreCase)
            ? (PreflightFinding.Warn(
                Platform, target,
                $"Reports {kernel}, not Linux. kubeadm and k0s install a Linux kubelet; this may not be a machine they can build on."),
                architecture)
            : (PreflightFinding.Pass(Platform, target, $"Linux on {architecture ?? "an unnamed architecture"}."),
                architecture);
    }

    private static async Task<PreflightFinding> PortsAsync(
        IPreflightProbe probe, string target, RemoteClusterHost host, string? cni, CancellationToken ct)
    {
        var wanted = Required(host, cni);

        // -H drops the header, -l listening only, -t TCP, -n numeric. One call for all of them: eight
        // round trips to ask about eight ports is eight times the latency for the same answer.
        var result = await probe.RunAsync("ss -Hltn", ct);

        if (!result.Ran)
            return PreflightFinding.Unknown(Ports, target, result.Failure ?? "The check could not be run.");

        if (!result.Ok)
        {
            return PreflightFinding.Unknown(
                Ports, target,
                "ss is not available here, so nothing could be said about the ports. Install iproute2, or check them by hand.");
        }

        var listening = Listening(result.Output);
        var taken = wanted.Where(p => listening.Contains(p.Port)).ToList();

        if (taken.Count == 0)
            return PreflightFinding.Pass(Ports, target, $"{Describe(wanted)} are free.");

        // No remedy: what is holding a port is another program, and killing it unasked is exactly the
        // kind of guess that turns a check into a liability.
        return PreflightFinding.Fail(
            Ports, target,
            $"Already in use: {string.Join(", ", taken.Select(t => $"{t.Port} ({t.What})"))}. "
            + "Something else is listening on a port Kubernetes needs; stop it, or pick another machine.");
    }

    private static async Task<PreflightFinding> SwapAsync(IPreflightProbe probe, string target, CancellationToken ct)
    {
        var result = await probe.RunAsync("swapon --noheadings --show=NAME", ct);

        if (!result.Ran)
            return PreflightFinding.Unknown(Swap, target, result.Failure ?? "The check could not be run.");

        // swapon exits non-zero on some builds when there is nothing to show; empty output is the
        // answer either way, and treating "no output" as unknown would fail every correct machine.
        if (result.Output.Length == 0)
            return PreflightFinding.Pass(Swap, target, "No swap is active.");

        var devices = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The one remedy that is genuinely unambiguous: the kubelet refuses to start with swap on, so
        // there is no configuration in which leaving it is the right answer, and there is one command.
        return PreflightFinding.Fail(
            Swap, target,
            $"Swap is on ({string.Join(", ", devices)}). The kubelet refuses to start while it is, so the "
            + "node would never come up.",
            new PreflightRemedy(
                "Turn swap off",
                "Runs swapoff -a now, and comments out the swap lines in /etc/fstab so it stays off after a reboot.",
                "sudo swapoff -a && sudo sed -i.kontena-bak '/\\sswap\\s/s/^/#/' /etc/fstab"));
    }

    private static async Task<PreflightFinding> ClockAsync(
        IPreflightProbe probe, string target, TimeProvider clock, CancellationToken ct)
    {
        var result = await probe.RunAsync("date +%s", ct);

        if (!result.Ran || !result.Ok)
            return PreflightFinding.Unknown(Clock, target, result.Failure ?? "date would not answer.");

        if (!long.TryParse(result.Output.Trim(), CultureInfo.InvariantCulture, out var seconds))
            return PreflightFinding.Unknown(Clock, target, $"Could not read the clock: '{result.Output}'.");

        var drift = (DateTimeOffset.FromUnixTimeSeconds(seconds) - clock.GetUtcNow()).Duration();

        if (drift <= ClockTolerance)
            return PreflightFinding.Pass(Clock, target, $"Within {drift.TotalSeconds:F0}s of this machine.");

        // A warning, and no remedy. Setting a clock means choosing a time source, which is a system-wide
        // decision belonging to whoever runs the machine — the same line the metrics-source install
        // draws when it declines to guess.
        return PreflightFinding.Warn(
            Clock, target,
            $"Off by {Drift(drift)}. Certificates are valid from a moment in time and etcd measures its own "
            + "health in milliseconds, so drift shows up later as expiry and election errors. Point it at NTP.");
    }

    private static async Task<(PreflightFinding Finding, string? Hostname, string? Uuid, IReadOnlyList<string> Macs)>
        IdentityAsync(IPreflightProbe probe, string target, CancellationToken ct)
    {
        // Three facts in one call, newline-separated in a fixed order, so a missing one is still
        // positional rather than guessed at.
        var result = await probe.RunAsync(
            "hostname; cat /sys/class/dmi/id/product_uuid 2>/dev/null || echo -; "
            + "cat /sys/class/net/*/address 2>/dev/null | grep -v '^00:00:00:00:00:00$' | sort -u | tr '\\n' ','",
            ct);

        if (!result.Ran || !result.Ok)
        {
            return (PreflightFinding.Unknown(
                Identity, target, result.Failure ?? "The machine's identity could not be read."), null, null, []);
        }

        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries);
        var hostname = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null;
        var uuid = lines.Length > 1 && lines[1] is { Length: > 0 } and not "-" ? lines[1] : null;
        var macs = lines.Length > 2
            ? lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        if (hostname is null)
            return (PreflightFinding.Unknown(Identity, target, "The machine would not say what it is called."), null, uuid, macs);

        // Uniqueness is decided across hosts; here we only confirm there is something to compare.
        return (PreflightFinding.Pass(Identity, target, $"Identifies as {hostname}."), hostname, uuid, macs);
    }

    // ── Across hosts ─────────────────────────────────────────────────────────

    /// <summary>
    /// The two questions no machine can answer about itself. Reported against the cluster rather than
    /// a host, because naming one of a colliding pair as the culprit would be arbitrary.
    /// </summary>
    private static IEnumerable<PreflightFinding> AcrossHosts(IReadOnlyList<HostPass> hosts)
    {
        const string cluster = "cluster";
        var known = hosts.Where(h => h.Hostname is not null).ToList();

        if (Duplicate(known, h => h.Hostname) is { } hostname)
        {
            yield return PreflightFinding.Fail(
                Identity, cluster,
                $"More than one machine calls itself {hostname}. Kubernetes names nodes by hostname, so the "
                + "second to join would overwrite the first — the classic result of cloning a VM without "
                + "resetting it. Give each machine its own hostname.");
        }
        else if (Duplicate(known, h => h.Uuid) is { } uuid)
        {
            yield return PreflightFinding.Fail(
                Identity, cluster,
                $"Two machines share the product_uuid {uuid}. That is the same clone, one layer down: "
                + "kubeadm reads it to tell nodes apart and will refuse. Recreate the VM rather than copying it.");
        }
        else if (known.SelectMany(h => h.Macs.Select(m => (Host: h, Mac: m)))
                     .GroupBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)
                     .FirstOrDefault(g => g.Select(x => x.Host.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                 is { } clash)
        {
            yield return PreflightFinding.Fail(
                Identity, cluster,
                $"Two machines have the MAC address {clash.Key}. On one network segment they cannot both be "
                + "reached reliably, and the cluster will look intermittently broken rather than plainly so.");
        }

        // Mixed architectures: said once, about the cluster, rather than on every row. It works — the
        // reason it is a warning at all is that images have to be multi-arch, and finding that out from
        // a CrashLoopBackOff is a bad afternoon.
        var architectures = hosts
            .Select(h => h.Architecture)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (architectures.Count > 1)
        {
            yield return PreflightFinding.Warn(
                Architecture, cluster,
                $"Machines of more than one architecture: {string.Join(" and ", architectures)}. This works — "
                + "Kubernetes schedules across them — but every image you run has to be multi-arch, or it will "
                + "only start on some nodes.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Which ports have to be free here, given the role and the planned CNI.</summary>
    internal static IReadOnlyList<(int Port, string What)> Required(RemoteClusterHost host, string? cni)
    {
        ArgumentNullException.ThrowIfNull(host);

        var ports = new List<(int, string)> { (10250, "kubelet") };

        if (host.Role == ClusterHostRole.Controller)
        {
            ports.Insert(0, (6443, "kube-apiserver"));
            ports.Add((2379, "etcd client"));
            ports.Add((2380, "etcd peer"));
        }

        // Calico speaks BGP between nodes; nothing else in the default set does.
        if (cni is { Length: > 0 } name && name.Contains("calico", StringComparison.OrdinalIgnoreCase))
            ports.Add((179, "Calico BGP"));

        return ports;
    }

    /// <summary>The local ports in <c>ss -Hltn</c> output. Handles IPv4, IPv6 and wildcard forms.</summary>
    internal static HashSet<int> Listening(string output)
    {
        var ports = new HashSet<int>();

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "LISTEN 0 4096 0.0.0.0:22 0.0.0.0:*" — the local address is the fourth column, and the
            // port is whatever follows its last colon, which is the one part IPv6 does not also contain.
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 4)
                continue;

            var local = columns[3];
            var colon = local.LastIndexOf(':');

            if (colon >= 0 && int.TryParse(local[(colon + 1)..], CultureInfo.InvariantCulture, out var port))
                ports.Add(port);
        }

        return ports;
    }

    private static string? Duplicate(IEnumerable<HostPass> hosts, Func<HostPass, string?> of) =>
        hosts.Select(of).OfType<string>()
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;

    private static string Describe(IReadOnlyList<(int Port, string What)> ports) =>
        string.Join(", ", ports.Select(p => p.Port.ToString(CultureInfo.InvariantCulture)));

    private static string Drift(TimeSpan drift) =>
        drift.TotalMinutes >= 1
            ? $"{drift.TotalMinutes:F0} minutes"
            : $"{drift.TotalSeconds:F0} seconds";
}
