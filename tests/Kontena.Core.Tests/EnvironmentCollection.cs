namespace Kontena.Core.Tests;

/// <summary>
/// Tests that read or write process-wide environment state (PATH, in practice) run one at a time.
/// <para>
/// xUnit runs test classes in parallel, so a locator test that empties PATH to prove the fallback
/// works would otherwise do that underneath a runner test trying to find <c>dotnet</c>. The failure
/// is real but the cause is the schedule, which is how a test earns the label "flaky" and then gets
/// ignored.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "process environment";
}
