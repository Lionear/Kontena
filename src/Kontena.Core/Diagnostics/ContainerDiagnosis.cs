using Kontena.Core.Models;
using Kontena.Sdk.Models;

namespace Kontena.Core.Diagnostics;

/// <summary>
/// Why this container is not running, read off the state the app already has (KON-150).
/// <para>
/// The engine side has fewer failure shapes than a cluster does — there is no scheduler and no
/// probe — so this is mostly about the two things an exit code cannot say on its own: whether the
/// kernel killed it, and whether it is going round in circles. Anything unrecognised returns
/// <c>null</c> rather than a guess.
/// </para>
/// </summary>
public static class ContainerDiagnosis
{
    /// <summary>How often a container may restart before "restarting" becomes "looping".</summary>
    private const int LoopThreshold = 3;

    /// <summary>
    /// Explain what is wrong with <paramref name="container"/>, or <c>null</c> when nothing is.
    /// <paramref name="inspect"/> carries the exit code and the OOM flag; <paramref name="stats"/> is
    /// the last live sample, used only for the memory limit it reports.
    /// </summary>
    public static Diagnosis? Diagnose(
        ContainerSummary container, ContainerInspect? inspect = null, ContainerStats? stats = null)
    {
        if (inspect is null)
            return null;

        return CannotStart(container, inspect)
               ?? OutOfMemory(container, inspect, stats)
               ?? RestartLoop(container, inspect)
               ?? ExitedBadly(container, inspect);
    }

    /// <summary>
    /// It never ran. A container whose command is not in the image stays <c>Created</c>: the runtime
    /// could not start the process, so there is no exit code to read and no log to open, and every
    /// other rule here is looking at a container that at least got going.
    /// </summary>
    private static Diagnosis? CannotStart(ContainerSummary container, ContainerInspect inspect)
    {
        if (container.State != ContainerState.Created || inspect.Error.Length == 0)
            return null;

        return new Diagnosis
        {
            Code = "CannotStart",
            Title = $"\"{container.Name}\" could not be started",
            // Deliberately not classified further. The runtime's message below is exact, and picking
            // it apart would mean matching on wording that is not API.
            Explanation = "The engine created the container but could not start the process inside it, so it never ran. The runtime's own message says what stopped it — most often a command that is not in the image, or a file that is not executable.",
            Evidence = [inspect.Error],
            Suggestion = "Check the command and entrypoint against what the image actually contains.",
            Action = DiagnosisAction.Inspect,
        };
    }

    /// <summary>The engine says the kernel killed it; the exit code alone never could.</summary>
    private static Diagnosis? OutOfMemory(ContainerSummary container, ContainerInspect inspect, ContainerStats? stats)
    {
        if (!inspect.OomKilled)
            return null;

        var evidence = new List<string>
        {
            $"Exit code: {inspect.ExitCode}{SignalSuffix(inspect.ExitCode)}",
            inspect.MemoryLimitBytes is { } limit
                ? $"Memory limit: {ByteSize.Format(limit)}"
                : "Memory limit: none set",
        };

        // The last live sample, when the page had one. Not a peak: neither engine reports the high
        // water mark on cgroup v2, and inventing one from the last sample would be a made-up number.
        if (stats is { MemoryUsedBytes: > 0 })
            evidence.Add($"Last measured use: {ByteSize.Format(stats.MemoryUsedBytes)}");
        if (inspect.RestartCount > 0)
            evidence.Add($"Restarts: {inspect.RestartCount}");

        // Whether the container itself was killed decides what its logs are worth. Exit 137 is the
        // container's own process taking SIGKILL: it never got to write anything. Any other code with
        // the OOM flag means something *inside* it was killed and the process then exited on its own —
        // and then the logs usually do say something, which the first wording sends you away from.
        var itselfKilled = inspect.ExitCode == 137;
        var ceiling = inspect.MemoryLimitBytes is { } bytes
            ? $"its memory limit of {ByteSize.Format(bytes)}"
            : "the memory available to it";

        return new Diagnosis
        {
            Code = "OOMKilled",
            Title = itselfKilled
                ? $"\"{container.Name}\" was killed for using too much memory"
                : $"Something in \"{container.Name}\" was killed for using too much memory",
            Explanation = itselfKilled
                ? $"It went over {ceiling} and the kernel killed it. The container did not choose to exit, so its own logs will not say why it stopped."
                : $"A process inside it went over {ceiling} and the kernel killed that process. The container then exited by itself with code {inspect.ExitCode}, so what it logged before stopping is worth reading.",
            Evidence = evidence,
            Suggestion = itselfKilled
                ? "Raise the container's memory limit, or find out what is holding the memory."
                : "The logs hold what it wrote before it stopped.",
            Action = DiagnosisAction.Logs,
        };
    }

    /// <summary>Started, failed, restarted — over and over, under its own restart policy.</summary>
    private static Diagnosis? RestartLoop(ContainerSummary container, ContainerInspect inspect)
    {
        var looping = container.State == ContainerState.Restarting || inspect.RestartCount >= LoopThreshold;
        if (!looping || inspect.RestartPolicy == RestartPolicy.No)
            return null;

        // A container that restarts on a schedule it was given, and succeeds, is not in trouble.
        if (inspect.ExitCode == 0 && container.State != ContainerState.Restarting)
            return null;

        var evidence = new List<string>
        {
            $"Restarts: {inspect.RestartCount}",
            $"Last exit code: {inspect.ExitCode}{SignalSuffix(inspect.ExitCode)}",
            $"Restart policy: {Policy(inspect.RestartPolicy)}",
        };

        return new Diagnosis
        {
            Code = "RestartLoop",
            Title = $"\"{container.Name}\" keeps restarting",
            Explanation = $"It exits and the engine starts it again, because its restart policy is {Policy(inspect.RestartPolicy)}. Whatever makes it exit happens on every run, so the logs of the run that ended are where the reason is.",
            Evidence = evidence,
            Suggestion = "Read the logs — they still hold the output of the runs that failed.",
            Action = DiagnosisAction.Logs,
        };
    }

    /// <summary>It stopped, and not because it was asked to.</summary>
    private static Diagnosis? ExitedBadly(ContainerSummary container, ContainerInspect inspect)
    {
        if (container.State is not (ContainerState.Exited or ContainerState.Dead) || inspect.ExitCode == 0)
            return null;

        // 143 is 128+SIGTERM: the shutdown signal a stop sends. That is a container that did as it was
        // told, and calling it a failure would put a warning on every container the user stopped.
        if (inspect.ExitCode == 143)
            return null;

        var evidence = new List<string> { $"Exit code: {inspect.ExitCode}{SignalSuffix(inspect.ExitCode)}" };
        if (inspect.FinishedAt is { } finished)
            evidence.Add($"Exited: {finished:u}");

        return new Diagnosis
        {
            Code = "NonZeroExit",
            Title = $"\"{container.Name}\" exited with code {inspect.ExitCode}",
            Explanation = inspect.ExitCode switch
            {
                126 => "The command exists but could not be run — usually a file that is not executable, or the wrong architecture.",
                127 => "The command was not found in the image. Check the entrypoint and the command against what the image actually contains.",
                139 => "The process crashed with a segmentation fault.",
                _ => "The process ended by itself with a non-zero status, which means it treated its own run as failed.",
            },
            Evidence = evidence,
            Suggestion = "The last lines it wrote are in the logs.",
            Action = DiagnosisAction.Logs,
        };
    }

    private static string Policy(RestartPolicy policy) => policy switch
    {
        RestartPolicy.Always => "always",
        RestartPolicy.OnFailure => "on-failure",
        RestartPolicy.UnlessStopped => "unless-stopped",
        _ => "no",
    };

    private static string SignalSuffix(int code) => code switch
    {
        137 => " (SIGKILL)",
        143 => " (SIGTERM)",
        139 => " (SIGSEGV)",
        _ => "",
    };
}
