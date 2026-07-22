using System.Runtime.CompilerServices;

// Driving the real CLIs is the part worth testing directly; the tests reach the runner here.
[assembly: InternalsVisibleTo("Kontena.Core.Orchestration.Tests")]

namespace Kontena.Core.Orchestration.Rendering;

/// <summary>
/// Anchor for the render sources (KON-88, KON-89) — the step in front of the declarative core.
/// A kustomization or a chart becomes flat YAML here, and from there it is an ordinary bundle:
/// same dry-run, same diff, same apply. Rendering deliberately knows nothing about clusters.
/// </summary>
public static class RenderingModule
{
    /// <summary>The renderers this build ships, in the order the UI offers them.</summary>
    public static IReadOnlyList<IManifestRenderer> All { get; } =
        [new KustomizeRenderer(), new HelmRenderer()];
}
