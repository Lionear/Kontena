using System.Globalization;
using Kontena.Core.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Turns a spec into the argument list for <c>minikube start</c>, and the small commands around it.
/// Split out from the provisioner so the command line can be asserted on without minikube, a
/// hypervisor or a container runtime.
/// </summary>
public static class MinikubeArguments
{
    /// <summary>
    /// Creating and starting are the same command in minikube: <c>start -p &lt;profile&gt;</c> makes the
    /// profile if it is new and brings it back up if it is not. The difference lives in the caller.
    /// </summary>
    public static IReadOnlyList<string> Create(LocalClusterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var arguments = new List<string> { "start", "--profile", spec.Name };

        if (!string.IsNullOrWhiteSpace(spec.KubernetesVersion))
        {
            arguments.Add("--kubernetes-version");
            arguments.Add(spec.KubernetesVersion);
        }

        // minikube counts every node, control-plane included; the spec counts them apart.
        var nodes = Math.Max(1, spec.ControlPlaneNodes) + spec.WorkerNodes;
        if (nodes > 1)
        {
            arguments.Add("--nodes");
            arguments.Add(Number(nodes));
        }

        if (spec.Cpus is { } cpus)
        {
            arguments.Add("--cpus");
            arguments.Add(Number(cpus));
        }

        if (spec.MemoryMb is { } memory)
        {
            arguments.Add("--memory");
            arguments.Add($"{Number(memory)}mb");
        }

        if (Driver(spec.Runtime) is { } driver)
        {
            arguments.Add("--driver");
            arguments.Add(driver);
        }

        // Only the container drivers can publish a host port; on a VM driver minikube says so itself,
        // and its own words are a better explanation than a rule we would have to keep in step.
        foreach (var mapping in spec.PortMappings)
        {
            arguments.Add("--ports");
            arguments.Add($"{Number(mapping.HostPort)}:{Number(mapping.ContainerPort)}/{mapping.Protocol.ToLowerInvariant()}");
        }

        return arguments;
    }

    /// <summary>Bring an existing profile back up.</summary>
    public static IReadOnlyList<string> Start(string name) => ["start", "--profile", name];

    /// <summary>Stop a profile, keeping it.</summary>
    public static IReadOnlyList<string> Stop(string name) => ["stop", "--profile", name];

    /// <summary>Remove a profile and everything in it.</summary>
    public static IReadOnlyList<string> Delete(string name) => ["delete", "--profile", name];

    /// <summary>List profiles as JSON — the only form of this output that is worth parsing.</summary>
    public static IReadOnlyList<string> List() => ["profile", "list", "--output", "json"];

    /// <summary>minikube's own name for a runtime, or null to let it choose.</summary>
    public static string? Driver(LocalClusterRuntime runtime) => runtime switch
    {
        LocalClusterRuntime.Docker => "docker",
        LocalClusterRuntime.Podman => "podman",
        LocalClusterRuntime.Kvm2 => "kvm2",
        _ => null,
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
