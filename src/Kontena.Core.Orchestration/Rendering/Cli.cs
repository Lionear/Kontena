using System.Diagnostics;
using System.Text;

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>What running an external tool produced: exit code plus both streams.</summary>
internal readonly record struct CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Whatever the tool said about failing — stderr, or stdout when stderr is empty.</summary>
    public string Complaint => StdErr.Length > 0 ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>Raised when the tool a renderer drives is not installed.</summary>
public sealed class ToolNotFoundException(string tool)
    : Exception($"'{tool}' was not found on PATH.")
{
    public string Tool { get; } = tool;
}

/// <summary>
/// Runs the render tools (kustomize, kubectl, helm) out of process. These are CLIs by nature —
/// there is no library form worth binding to — so the invocation lives in one place: no shell,
/// an argument list rather than a command string (nothing to quote, nothing to inject), and both
/// streams captured so a failure can be reported in the tool's own words.
/// </summary>
internal static class Cli
{
    /// <summary>A render that hasn't finished by now is stuck, not slow.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<CliResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new ToolNotFoundException(exe);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        // Read both streams concurrently: a tool that fills one pipe while we drain the other
        // would otherwise deadlock on a chatty render.
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new CliResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>The absolute path of <paramref name="exe"/> on PATH, or null when it isn't there.</summary>
    public static string? Locate(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".cmd", exe } : [exe];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Render an invocation the way a user would type it, so a render can be reproduced.</summary>
    public static string Describe(string exe, IReadOnlyList<string> args)
    {
        var text = new StringBuilder(Path.GetFileNameWithoutExtension(exe));
        foreach (var arg in args)
        {
            text.Append(' ');
            text.Append(arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg);
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill — nothing to clean up.
        }
    }
}
