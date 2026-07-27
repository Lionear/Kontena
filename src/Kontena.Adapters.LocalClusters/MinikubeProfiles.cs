using System.Text.Json;
using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Reads <c>minikube profile list -o json</c>.
/// <para>
/// Deliberately defensive: every field is optional here, and a profile with nothing but a name still
/// becomes a row. minikube's JSON is not a documented contract, it has gained and moved fields between
/// releases, and the alternative to reading it loosely is a list that empties itself on an upgrade.
/// </para>
/// </summary>
public static class MinikubeProfiles
{
    /// <summary>
    /// The profiles in this output. Invalid ones are skipped: minikube reports them separately, and
    /// they are leftovers of a failed create rather than clusters anyone can use.
    /// </summary>
    public static IReadOnlyList<LocalCluster> Parse(string json, string provisioner)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("valid", out var valid)
                || valid.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. valid.EnumerateArray().Select(p => Read(p, provisioner)).OfType<LocalCluster>()];
        }
        catch (JsonException)
        {
            // A tool that answered with something else entirely is a broken install, not an empty list
            // — but the caller is only asking which clusters exist, and none we can name is the honest
            // answer to that question.
            return [];
        }
    }

    private static LocalCluster? Read(JsonElement profile, string provisioner)
    {
        var config = profile.TryGetProperty("Config", out var c) ? c : default;

        // The name lives on the profile in some versions and in its Config in others; either will do,
        // and without one there is nothing to show or to delete.
        var name = Text(profile, "Name") ?? Text(config, "Name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new LocalCluster(name, provisioner, name)
        {
            // minikube names the kubeconfig context after the profile, which is why Context is the
            // name again here rather than a prefixed form like kind's.
            State = StateOf(Text(profile, "Status")),
            Driver = Text(config, "Driver"),
            Nodes = NodesOf(config),
        };
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// minikube's status words, mapped onto the three states Kontena has. Anything it invents later
    /// lands on Unknown rather than being guessed into Running — an unknown state costs a greyed-out
    /// button; a wrong one costs a Start that does nothing.
    /// <para>
    /// <c>OK</c> is what a healthy profile actually reports here (measured against minikube v1.38.1):
    /// this field is the rollup over the profile's components, not a machine state, and it only reads
    /// <c>Running</c> in the per-node output. Reading it as unknown left every running cluster without
    /// its Stop button (KON-142).
    /// </para>
    /// </summary>
    private static LocalClusterState StateOf(string? status) => status switch
    {
        "OK" or "Running" => LocalClusterState.Running,
        "Stopped" or "Paused" => LocalClusterState.Stopped,
        _ => LocalClusterState.Unknown,
    };

    private static IReadOnlyList<string> NodesOf(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("Nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. nodes.EnumerateArray()
                .Select((n, i) => Text(n, "Name") is { Length: > 0 } named
                    ? named
                    // The first node's name is empty in minikube's own output; it is still a node.
                    : $"node-{i + 1}"),
        ];
    }
}
