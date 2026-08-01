using Kontena.Sdk.Models;

namespace Kontena.App.Services;

/// <summary>
/// A curated environment variable in a <see cref="ContainerRecipe"/> — the piece image
/// metadata cannot express. <paramref name="Required"/> vars block Run until filled.
/// </summary>
public sealed record RecipeEnv(string Key, bool Required = true, string? Placeholder = null);

/// <summary>
/// A curated pre-fill recipe for a popular image: the required env vars (not machine-readable
/// from image metadata), a suggested container name, and sensible default ports/volumes to add
/// on top of what the image's own metadata already scaffolds.
/// </summary>
public sealed record ContainerRecipe(
    string[] Match,
    string? SuggestedName = null,
    RecipeEnv[]? Env = null,
    PortBinding[]? Ports = null,
    string[]? Volumes = null)
{
    public IReadOnlyList<RecipeEnv> Environment => Env ?? [];
    public IReadOnlyList<PortBinding> DefaultPorts => Ports ?? [];
    public IReadOnlyList<string> DefaultVolumes => Volumes ?? [];
}

/// <summary>
/// Curated, data-driven catalog of run recipes for popular images. Add a row to extend it;
/// the shape is kept data-first so it can later be loaded from / overridden by JSON (parallels
/// SQL Explorer's provider container-recipes, SE-166).
/// </summary>
public static class RecipeCatalog
{
    private static readonly ContainerRecipe[] Recipes =
    [
        new(
            Match: ["postgres", "postgresql"],
            SuggestedName: "postgres",
            Env: [new("POSTGRES_PASSWORD", Required: true, "a strong password")],
            Ports: [new(5432, 5432)],
            Volumes: ["/var/lib/postgresql/data"]),
        new(
            Match: ["mysql"],
            SuggestedName: "mysql",
            Env: [new("MYSQL_ROOT_PASSWORD", Required: true, "the root password")],
            Ports: [new(3306, 3306)],
            Volumes: ["/var/lib/mysql"]),
        new(
            Match: ["mariadb"],
            SuggestedName: "mariadb",
            Env: [new("MARIADB_ROOT_PASSWORD", Required: true, "the root password")],
            Ports: [new(3306, 3306)],
            Volumes: ["/var/lib/mysql"]),
        new(
            Match: ["mongo", "mongodb"],
            SuggestedName: "mongo",
            Ports: [new(27017, 27017)],
            Volumes: ["/data/db"]),
        new(
            Match: ["redis"],
            SuggestedName: "redis",
            Ports: [new(6379, 6379)],
            Volumes: ["/data"]),
        new(
            Match: ["rabbitmq"],
            SuggestedName: "rabbitmq",
            Ports: [new(5672, 5672), new(15672, 15672)]),
        new(
            Match: ["nginx"],
            SuggestedName: "nginx",
            Ports: [new(8080, 80)]),
        new(
            Match: ["httpd"],
            SuggestedName: "httpd",
            Ports: [new(8080, 80)]),
    ];

    /// <summary>
    /// Find a recipe for an image reference. Registry, namespace and tag/digest are ignored —
    /// e.g. <c>docker.io/library/postgres:16</c> and <c>postgres</c> both match the postgres recipe.
    /// </summary>
    public static ContainerRecipe? Match(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
            return null;

        var reference = imageReference.Trim();

        // Strip digest (@sha256:…) and tag (:tag, but not a registry port).
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
            reference = reference[..at];

        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        if (colon > slash)
            reference = reference[..colon];

        reference = reference.ToLowerInvariant();
        var leaf = reference.Contains('/', StringComparison.Ordinal)
            ? reference[(reference.LastIndexOf('/') + 1)..]
            : reference;

        return Recipes.FirstOrDefault(r =>
            r.Match.Any(m => string.Equals(m, leaf, StringComparison.Ordinal)));
    }
}
