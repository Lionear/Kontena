namespace Kontena.Adapters.Docker;

/// <summary>
/// Placeholder anchor for the Docker adapter (KON-27). Will implement the
/// CEAL contract against the Docker Engine API over the Unix socket /
/// Windows named pipe.
/// </summary>
public static class DockerAdapterModule
{
    /// <summary>Backend identifier used by the engine registry.</summary>
    public const string BackendId = "docker";
}
