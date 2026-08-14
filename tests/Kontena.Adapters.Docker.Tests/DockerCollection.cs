using Xunit;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// Every class that talks to a real daemon belongs here, so they run one after another. They share one
/// machine: <c>Browsing_leaves_no_container_behind</c> counts all containers before and after, and any
/// other test creating one at that moment makes it fail for a reason that has nothing to do with it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DockerCollection
{
    public const string Name = "docker daemon";
}
