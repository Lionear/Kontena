using System.Globalization;
using System.Text;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Writes the <c>kind: Cluster</c> config file that <c>kind create cluster --config</c> reads.
/// <para>
/// Only for what the flags cannot express. A single-node cluster needs no config at all, and
/// <see cref="Needed"/> says so — writing one anyway would mean every create depends on this file
/// being right, including the plain case that has nothing to get wrong.
/// </para>
/// <para>
/// Hand-written rather than serialized: it is a dozen lines of a schema that is not ours, and a YAML
/// dependency would have to be told the same shape anyway.
/// </para>
/// </summary>
public static class KindConfig
{
    /// <summary>Whether this spec needs a config file, or fits in the command-line flags.</summary>
    public static bool Needed(LocalClusterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.ControlPlaneNodes > 1
               || spec.WorkerNodes > 0
               || spec.PortMappings.Count > 0
               || spec.IngressReady;
    }

    /// <summary>
    /// The config for this spec. Port mappings and the ingress label go on the <b>first</b>
    /// control-plane node only: both are about one addressable node, and kind's own ingress guide
    /// pins them to the same one so a controller has somewhere to land.
    /// </summary>
    public static string Write(LocalClusterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var yaml = new StringBuilder()
            .AppendLine("kind: Cluster")
            .AppendLine("apiVersion: kind.x-k8s.io/v1alpha4")
            .AppendLine("nodes:");

        for (var i = 0; i < Math.Max(1, spec.ControlPlaneNodes); i++)
        {
            yaml.AppendLine("  - role: control-plane");
            if (i == 0)
                First(yaml, spec);
        }

        for (var i = 0; i < spec.WorkerNodes; i++)
            yaml.AppendLine("  - role: worker");

        return yaml.ToString();
    }

    private static void First(StringBuilder yaml, LocalClusterSpec spec)
    {
        if (spec.IngressReady)
        {
            yaml.AppendLine("    kubeadmConfigPatches:")
                .AppendLine("      - |")
                .AppendLine("        kind: InitConfiguration")
                .AppendLine("        nodeRegistration:")
                .AppendLine("          kubeletExtraArgs:")
                .AppendLine("            node-labels: \"ingress-ready=true\"");
        }

        if (spec.PortMappings.Count == 0)
            return;

        yaml.AppendLine("    extraPortMappings:");
        foreach (var mapping in spec.PortMappings)
        {
            yaml.AppendLine(Line("      - containerPort: {0}", mapping.ContainerPort))
                .AppendLine(Line("        hostPort: {0}", mapping.HostPort))
                // kind wants the protocol upper-case here, unlike the lower-case spelling the rest of
                // Kontena uses for a container's ports.
                .AppendLine(CultureInfo.InvariantCulture, $"        protocol: {mapping.Protocol.ToUpperInvariant()}");

            if (!string.IsNullOrWhiteSpace(mapping.ListenAddress))
                yaml.AppendLine(CultureInfo.InvariantCulture, $"        listenAddress: \"{mapping.ListenAddress}\"");
        }
    }

    private static string Line(string format, int value)
        => string.Format(CultureInfo.InvariantCulture, format, value);
}
