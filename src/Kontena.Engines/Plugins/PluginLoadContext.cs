using System.Reflection;
using System.Runtime.Loader;

namespace Kontena.Engines.Plugins;

/// <summary>
/// One load context per plugin directory, so two plugins may depend on different versions of the same
/// library without either being told about the other.
/// <para>
/// The rule that carries the whole design is the <em>refusal</em> in <see cref="Load"/>: anything the
/// host has already loaded — <c>Kontena.Sdk</c> above all — resolves to null here and comes from the
/// default context instead. A plugin ships its own <c>Kontena.Sdk.dll</c> as a matter of course, and
/// loading it would give the plugin an <c>IEnginePlugin</c> that is a different type from the host's:
/// the cast yields null, nothing registers, and nothing is thrown. That is the one failure in this
/// subsystem that leaves no trace, which is why it is prevented here rather than detected later.
/// </para>
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);

    /// <summary>
    /// Resolve a dependency out of the plugin's own directory — unless the default context already has
    /// it, in which case return null so the runtime falls back to that one shared copy.
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsLoadedByHost(assemblyName))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsLoadedByHost(AssemblyName assemblyName) =>
        Default.Assemblies.Any(a =>
            string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
}
