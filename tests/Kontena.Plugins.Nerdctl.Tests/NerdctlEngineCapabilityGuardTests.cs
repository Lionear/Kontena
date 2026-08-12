using System.Reflection;
using Kontena.Sdk;
using Kontena.Sdk.Models;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;

namespace Kontena.Plugins.Nerdctl.Tests;

/// <summary>
/// Walks every <see cref="EngineCapabilities"/> flag by reflection and pairs each one with the method(s)
/// <see cref="IContainerEngine"/>'s own <c>&lt;remarks&gt;Requires ...&lt;/remarks&gt;</c> docs say it
/// guards (KON-141 PR 3 task 5). A flag that reports <c>true</c> must correspond to a method that does
/// not throw <see cref="NotSupportedException"/>; a flag that reports <c>false</c> must correspond to
/// one that still does. A bare "the flag is false" assertion proves nothing — <c>bool</c> defaults to
/// false, so that would pass whether or not the flag was ever wired up at all (the exact shape this
/// branch has already caught four times); pairing the flag with the method's actual behaviour is what
/// makes it real.
/// <para>
/// Reflection over <see cref="EngineCapabilities"/>'s properties, rather than one hand-written assertion
/// per flag, is what makes <see cref="Every_EngineCapabilities_property_is_either_probed_or_explicitly_named_as_unguarded"/>
/// fail the moment the record gains a property nobody wrote a probe for — a hand-written list would
/// simply never look at it.
/// </para>
/// <para>
/// <see cref="EngineCapabilities.Rootless"/> and <see cref="EngineCapabilities.SupportsGpu"/> are named
/// in <see cref="Unguarded"/> rather than silently skipped: Rootless is an observation read off
/// <c>info</c>, not a promise about a method (already covered by NerdctlEngineTests), and SupportsGpu has
/// no <c>&lt;remarks&gt;Requires ...&lt;/remarks&gt;</c> member anywhere in <see cref="IContainerEngine"/>
/// — no adapter implements GPU passthrough yet, so there is no method to pair it with. The day either
/// gains one, the coverage test above forces whoever adds it to move the name into <see cref="Probes"/>.
/// </para>
/// </summary>
public sealed class NerdctlEngineCapabilityGuardTests
{
    private static FakeToolRunner Installed() => new FakeToolRunner().Install(NerdctlTool.Definition);

    private static NerdctlEngine Engine(IToolRunner runner, string @namespace = "k8s.io") =>
        new(new NerdctlCli(runner, @namespace), $"nerdctl:{@namespace}", $"nerdctl ({@namespace})", @namespace);

    /// <summary>Flags with no guarded method in <see cref="IContainerEngine"/> — see the class remarks.</summary>
    private static readonly HashSet<string> Unguarded =
        [nameof(EngineCapabilities.Rootless), nameof(EngineCapabilities.SupportsGpu)];

    /// <summary>
    /// One probe per method <see cref="IContainerEngine"/>'s <c>&lt;remarks&gt;</c> ties to a capability
    /// flag. Each probe runs the method and the test below checks only whether it threw
    /// <see cref="NotSupportedException"/> — not whether it succeeded — because that is the only thing a
    /// capability flag promises: <see cref="EngineCapabilities.SupportsPrune"/>'s methods are real CLI
    /// calls that can still fail for other reasons (a missing binary, a non-zero exit) without breaking
    /// their promise to no longer be a stub.
    /// </summary>
    private static readonly Dictionary<string, Func<NerdctlEngine, Task>[]> Probes = new()
    {
        [nameof(EngineCapabilities.SupportsBuild)] =
        [
            async e =>
            {
                await foreach (var _ in e.BuildImageAsync(new BuildRequest { ContextPath = ".", Tag = "x" }))
                {
                }
            },
        ],
        [nameof(EngineCapabilities.SupportsCompose)] =
        [
            async e =>
            {
                await foreach (var _ in e.ComposeUpAsync(new ComposeUpRequest { ComposeFilePath = "compose.yaml" }))
                {
                }
            },
        ],
        [nameof(EngineCapabilities.SupportsExec)] =
        [
            e => e.ExecAsync("id", new ExecRequest { Command = ["echo"] }).AsTask(),
            e => e.StartExecSessionAsync("id", new ExecRequest { Command = ["echo"] }).AsTask(),
        ],
        [nameof(EngineCapabilities.SupportsPrune)] =
        [
            e => e.PruneContainersAsync().AsTask(),
            e => e.PruneImagesAsync().AsTask(),
            e => e.PruneVolumesAsync().AsTask(),
        ],
        [nameof(EngineCapabilities.SupportsVolumeBrowse)] =
        [
            e => e.BrowseVolumeAsync("v").AsTask(),
        ],
        [nameof(EngineCapabilities.SupportsVolumeTransfer)] =
        [
            e => e.ExportVolumeAsync("v", "/tmp/v.tar").AsTask(),
            e => e.ImportVolumeAsync("v", "/tmp/v.tar").AsTask(),
        ],
        [nameof(EngineCapabilities.SupportsRestartPolicy)] =
        [
            // The policy has to be a real one: `No` is what every engine does anyway, so a request
            // carrying it would pass this probe even on an engine that cannot honour any other.
            e => e.CreateContainerAsync(new CreateContainerRequest
            {
                Image = "alpine",
                RestartPolicy = RestartPolicy.Always,
            }).AsTask(),
        ],
        [nameof(EngineCapabilities.SupportsStats)] =
        [
            async e =>
            {
                await foreach (var _ in e.StreamStatsAsync("id"))
                {
                }
            },
        ],
        [nameof(EngineCapabilities.SupportsEvents)] =
        [
            async e =>
            {
                await foreach (var _ in e.StreamEventsAsync())
                {
                }
            },
        ],
    };

    [Fact]
    public void Every_EngineCapabilities_property_is_either_probed_or_explicitly_named_as_unguarded()
    {
        var properties = typeof(EngineCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool));

        foreach (var property in properties)
        {
            Assert.True(
                Probes.ContainsKey(property.Name) || Unguarded.Contains(property.Name),
                $"EngineCapabilities.{property.Name} is neither probed nor listed in Unguarded — " +
                "add a probe for the method(s) it guards, or name it in Unguarded with a reason.");
        }
    }

    [Fact]
    public async Task Every_probed_capability_flag_matches_whether_its_guarded_method_throws_NotSupportedException()
    {
        // A single successful, empty-output runner is enough for every probe: the still-unimplemented
        // members throw before ever touching the runner, and SupportsPrune's real methods only need the
        // tool to be installed and exit zero to prove they no longer throw NotSupportedException.
        var engine = Engine(Installed().When(_ => true, output: []));
        var capabilities = engine.Capabilities;

        foreach (var (flagName, probes) in Probes)
        {
            var claims = (bool)typeof(EngineCapabilities).GetProperty(flagName)!.GetValue(capabilities)!;

            foreach (var probe in probes)
            {
                var threwNotSupported = false;
                try
                {
                    await probe(engine);
                }
                catch (NotSupportedException)
                {
                    threwNotSupported = true;
                }
                catch (Exception)
                {
                    // Any other failure is this runner being a stub, not the flag being wrong — which is
                    // what the remarks above already promise this test ignores. CreateContainerAsync is
                    // the case that made the promise real: it has to read an id back, and an
                    // empty-output runner cannot give it one.
                }

                Assert.True(
                    claims == !threwNotSupported,
                    $"EngineCapabilities.{flagName} reports {claims}, but its guarded method " +
                    $"{(threwNotSupported ? "threw" : "did not throw")} NotSupportedException.");
            }
        }
    }
}
