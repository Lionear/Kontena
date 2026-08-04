using System.Reflection;
using System.Text.RegularExpressions;

namespace Kontena.Sdk.Tests;

/// <summary>
/// The rule <c>CONTRIBUTING.md</c> §4 states — an adapter implements the abstraction layer and
/// references only <c>Kontena.Sdk</c> — checked against the project files rather than trusted (KON-190).
/// <para>
/// It was untrue for a year and nothing noticed: <c>Kontena.Sdk</c> was two interfaces stacked on top of
/// <c>Kontena.Core</c> and <c>Kontena.Engines</c>, every adapter referenced those two directly, and the
/// SDK itself had no consumer. A rule external contributors are held to has to be one the build can
/// disagree with.
/// </para>
/// <para>
/// The licence split rests on the same fact. <c>src/Kontena.Sdk/LICENSE</c> is MIT so that a third party
/// may write — and sell — a backend; everything else carries the Commons Clause, which forbids selling.
/// That promise only holds while the SDK compiles against nothing but the framework, so
/// <see cref="The_sdk_references_no_other_project"/> is a licence test as much as an architecture one.
/// </para>
/// </summary>
public sealed class ExtensionBoundaryTests
{
    /// <summary>An adapter may lean on another adapter — Podman speaks the Docker API and reuses it.</summary>
    private static bool IsAllowedFromAdapter(string project) =>
        project is "Kontena.Sdk" || project.StartsWith("Kontena.Adapters.", StringComparison.Ordinal);

    [Fact]
    public void The_sdk_references_no_other_project()
    {
        var refs = ProjectReferences(Path.Combine(SourceDirectory(), "Kontena.Sdk", "Kontena.Sdk.csproj"));

        Assert.True(
            refs.Length == 0,
            $"Kontena.Sdk is the MIT extension contract and must stand on its own, but references: "
            + string.Join(", ", refs));
    }

    [Theory]
    [MemberData(nameof(AdapterProjects))]
    public void An_adapter_references_only_the_sdk(string adapter)
    {
        var offenders = ProjectReferences(Path.Combine(SourceDirectory(), adapter, adapter + ".csproj"))
            .Where(r => !IsAllowedFromAdapter(r))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{adapter} may only reference Kontena.Sdk (or another adapter), but also references: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A plugin (KON-286, Manifest Studio is the first) has no sibling to lean on the way Podman leans
    /// on Docker — it never touches another backend, so unlike <see cref="IsAllowedFromAdapter"/> the
    /// only allowed reference is the SDK itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(PluginProjects))]
    public void A_plugin_references_only_the_sdk(string plugin)
    {
        var offenders = ProjectReferences(Path.Combine(SourceDirectory(), plugin, plugin + ".csproj"))
            .Where(r => r != "Kontena.Sdk")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{plugin} may only reference Kontena.Sdk, but also references: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A type shipped in the SDK assembly announces itself as SDK. Moving files is easy and moving the
    /// namespace with them is easy to forget, and the result compiles: a <c>Kontena.Core.Models</c> type
    /// living in <c>Kontena.Sdk.dll</c> reads to an adapter author exactly like the confusion this
    /// refactor removed.
    /// </summary>
    [Fact]
    public void Every_public_sdk_type_lives_in_an_sdk_namespace()
    {
        var strays = typeof(IEnginePlugin).Assembly.GetExportedTypes()
            .Where(t => t.Namespace is not { } ns
                        || (ns != "Kontena.Sdk" && !ns.StartsWith("Kontena.Sdk.", StringComparison.Ordinal)))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.True(strays.Length == 0, "Types outside a Kontena.Sdk namespace: " + string.Join(", ", strays));
    }

    public static TheoryData<string> AdapterProjects()
    {
        var data = new TheoryData<string>();

        foreach (var dir in Directory.GetDirectories(SourceDirectory(), "Kontena.Adapters.*").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(dir));

        return data;
    }

    public static TheoryData<string> PluginProjects()
    {
        var data = new TheoryData<string>();

        foreach (var dir in Directory.GetDirectories(SourceDirectory(), "Kontena.Plugins.*").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(dir));

        return data;
    }

    private static string[] ProjectReferences(string csproj) =>
        Regex.Matches(File.ReadAllText(csproj), @"<ProjectReference\s+Include=""[^""]*[\\/](?<name>[^\\/""]+)\.csproj""")
            .Select(m => m.Groups["name"].Value)
            .ToArray();

    private static string SourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(src, "Kontena.Sdk")))
                return src;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/ from the test output directory.");
    }
}
