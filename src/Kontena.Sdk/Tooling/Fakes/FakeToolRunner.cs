namespace Kontena.Sdk.Tooling.Fakes;

/// <summary>
/// An <see cref="IToolRunner"/> that runs nothing. Lets the code that drives kind, minikube or kubectl
/// be tested on a machine that has none of them — which is every CI runner, and most laptops.
/// <para>
/// Scripted rather than clever: say which tools exist, say what each invocation prints, and assert on
/// what was asked for afterwards. A fake that guesses at plausible output would let a wrong command
/// line pass.
/// </para>
/// </summary>
public sealed class FakeToolRunner : IToolRunner
{
    private readonly Dictionary<string, string?> _installed = new(StringComparer.Ordinal);
    private readonly List<Func<ToolInvocation, ScriptedRun?>> _scripts = [];

    /// <summary>Every invocation that was asked for, in order — the point of the fake.</summary>
    public List<ToolInvocation> Invocations { get; } = [];

    /// <summary>Pretend <paramref name="tool"/> is installed, answering <paramref name="version"/>.</summary>
    public FakeToolRunner Install(ExternalTool tool, string version = "v1.0.0")
    {
        _installed[tool.Executable] = version;
        return this;
    }

    /// <summary>
    /// Pretend the tool is there but broken — found on disk, refuses to say what it is. Worth having
    /// its own case: "install it" is the wrong advice for a binary that exists.
    /// </summary>
    public FakeToolRunner InstallBroken(ExternalTool tool)
    {
        _installed[tool.Executable] = null;
        return this;
    }

    /// <summary>
    /// Script what an invocation produces. The first matching script wins, so a specific case can be
    /// added in front of a general one.
    /// </summary>
    public FakeToolRunner When(
        Func<ToolInvocation, bool> matches,
        IEnumerable<string>? output = null,
        int exitCode = 0,
        IEnumerable<string>? errorOutput = null)
    {
        var run = new ScriptedRun([.. output ?? []], [.. errorOutput ?? []], exitCode);
        _scripts.Add(invocation => matches(invocation) ? run : null);
        return this;
    }

    public ValueTask<ToolLocation> FindAsync(ExternalTool tool, CancellationToken ct = default)
    {
        if (!_installed.TryGetValue(tool.Executable, out var version))
            return ValueTask.FromResult(ToolLocation.Missing(tool));

        return ValueTask.FromResult(new ToolLocation(tool, $"/fake/bin/{tool.Executable}", version));
    }

    public ValueTask<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        var run = Resolve(invocation);
        return ValueTask.FromResult(new ToolResult(
            run.ExitCode,
            string.Join(System.Environment.NewLine, run.Output),
            string.Join(System.Environment.NewLine, run.ErrorOutput)));
    }

    public async IAsyncEnumerable<ToolLine> StreamAsync(
        ToolInvocation invocation,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var run = Resolve(invocation);

        foreach (var line in run.Output)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ToolLine(ToolOutputKind.Out, line);
        }

        foreach (var line in run.ErrorOutput)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ToolLine(ToolOutputKind.Error, line);
        }

        await Task.CompletedTask;

        if (run.ExitCode != 0)
            throw new ToolFailedException(invocation.CommandLine, run.ExitCode, string.Join('\n', run.ErrorOutput));
    }

    private ScriptedRun Resolve(ToolInvocation invocation)
    {
        Invocations.Add(invocation);

        if (!_installed.ContainsKey(invocation.Tool.Executable))
            throw new ToolNotFoundException(invocation.Tool.Name);

        foreach (var script in _scripts)
            if (script(invocation) is { } run)
                return run;

        // Unscripted but installed: succeeded, said nothing. Callers that care assert on Invocations.
        return new ScriptedRun([], [], 0);
    }

    private sealed record ScriptedRun(
        IReadOnlyList<string> Output, IReadOnlyList<string> ErrorOutput, int ExitCode);
}
