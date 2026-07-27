namespace Kontena.Core.Tooling.Fakes;

/// <summary>
/// A release source that answers from memory (KON-153).
/// <para>
/// Exists so that no test reaches the network by accident. The update check runs in the background
/// whenever the tooling page loads, which means every test that builds that page would otherwise be
/// making real requests to a publisher's API — slow, flaky, rate-limited, and invisible until it
/// starts failing for reasons that have nothing to do with the test.
/// </para>
/// </summary>
public sealed class FakeToolReleaseSource : IToolReleaseSource
{
    private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);

    /// <summary>How often it was asked — the cache's behaviour is worth asserting on.</summary>
    public int Calls { get; private set; }

    /// <summary>Publish a release for this tool. Anything not published answers "nothing to offer".</summary>
    public FakeToolReleaseSource Publish(ExternalTool tool, string version)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _versions[tool.Name] = version;
        return this;
    }

    public ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Calls++;

        return ValueTask.FromResult(_versions.TryGetValue(tool.Name, out var version)
            ? new ToolDownload(tool, version, new Uri("https://example.invalid/release"), new string('a', 64))
            : null);
    }
}
