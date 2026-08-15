using Kontena.Core.Orchestration.Preflight;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>
/// A machine that answers whatever a test says it answers. The point of the probe seam: every check
/// can be exercised — including the ones about clones, clock drift and busy ports — without a machine.
/// </summary>
public sealed class FakePreflightProbe(string target) : IPreflightProbe
{
    private readonly Dictionary<string, ProbeResult> _answers = new(StringComparer.Ordinal);

    public string Target { get; } = target;

    /// <summary>Every command it was asked to run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>
    /// What an unmatched command answers. Defaults to a clean exit with no output, so a test only has
    /// to say what is interesting about its machine.
    /// </summary>
    public ProbeResult Default { get; set; } = ProbeResult.Success();

    /// <summary>Answers this whenever a command contains <paramref name="fragment"/>.</summary>
    public FakePreflightProbe Answer(string fragment, ProbeResult result)
    {
        _answers[fragment] = result;
        return this;
    }

    /// <summary>
    /// Nothing runs — the machine is not there. Clears any canned answers as well as setting the
    /// default: a machine that cannot be reached answers nothing, and leaving earlier
    /// <see cref="Answer"/> calls standing would let a test build a host that is both unreachable and
    /// talkative.
    /// </summary>
    public FakePreflightProbe Unreachable(string why = "No route to host.")
    {
        _answers.Clear();
        Default = ProbeResult.Unreachable(why);
        return this;
    }

    public ValueTask<ProbeResult> RunAsync(string command, CancellationToken ct = default)
    {
        Commands.Add(command);

        // Longest fragment wins, so a specific answer beats a general one however they were registered.
        // Insertion order would make "swapon" shadow "swapon --show", which is a trap rather than a rule.
        var match = _answers
            .Where(a => command.Contains(a.Key, StringComparison.Ordinal))
            .OrderByDescending(a => a.Key.Length)
            .Select(a => (ProbeResult?)a.Value)
            .FirstOrDefault();

        return ValueTask.FromResult(match ?? Default);
    }
}
