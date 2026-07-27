using System.Globalization;
using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Turns a spec into the argument list for <c>kind create cluster</c>. Split out from the provisioner
/// so the command line can be asserted on without a kind, a runtime or a temp directory.
/// </summary>
public static class KindArguments
{
    /// <summary>
    /// The create arguments. <paramref name="configPath"/> is the file
    /// <see cref="KindConfig.Write"/> produced, or null when the spec needed none.
    /// </summary>
    public static IReadOnlyList<string> Create(LocalClusterSpec spec, string? configPath)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var arguments = new List<string> { "create", "cluster", "--name", spec.Name };

        // An explicit image wins over a version: it can carry a mirror or a digest, and the version
        // field can express neither.
        if (NodeImage(spec) is { } image)
        {
            arguments.Add("--image");
            arguments.Add(image);
        }

        if (configPath is not null)
        {
            arguments.Add("--config");
            arguments.Add(configPath);
        }

        if (spec.ReadyTimeout is { } timeout)
        {
            arguments.Add("--wait");
            arguments.Add($"{((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s");
        }

        return arguments;
    }

    /// <summary>The arguments that remove a cluster.</summary>
    public static IReadOnlyList<string> Delete(string name) => ["delete", "cluster", "--name", name];

    /// <summary>The arguments that list what kind owns.</summary>
    public static IReadOnlyList<string> List() => ["get", "clusters"];

    /// <summary>
    /// Which node image to ask for, or null to let kind use the one its release was built against.
    /// </summary>
    private static string? NodeImage(LocalClusterSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.NodeImage))
            return spec.NodeImage;

        if (string.IsNullOrWhiteSpace(spec.KubernetesVersion))
            return null;

        var version = spec.KubernetesVersion.StartsWith('v')
            ? spec.KubernetesVersion
            : $"v{spec.KubernetesVersion}";

        return $"kindest/node:{version}";
    }
}
