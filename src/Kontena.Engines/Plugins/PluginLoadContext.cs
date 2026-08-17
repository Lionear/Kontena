using System.Reflection;
using System.Runtime.Loader;

namespace Kontena.Engines.Plugins;

/// <summary>
/// One load context per plugin directory, so two plugins may depend on different versions of the same
/// library without either being told about the other — <em>for libraries the host does not itself
/// load</em>. Where a plugin and the host both carry a copy, the host's copy wins (see the refusal in
/// <see cref="Load"/> below) and the plugin's own copy is discarded, version and all.
/// <para>
/// The rule that carries the whole design is that <em>refusal</em>: anything the host has already
/// loaded — <c>Kontena.Sdk</c> above all — resolves to null here and comes from the default context
/// instead. A plugin ships its own <c>Kontena.Sdk.dll</c> as a matter of course, and loading it would
/// give the plugin an <c>IEnginePlugin</c> that is a different type from the host's: the cast yields
/// null, nothing registers, and nothing is thrown. That is the one failure in this subsystem that
/// leaves no trace, which is why it is prevented here rather than detected later.
/// </para>
/// <para>
/// The cost of that choice: isolation holds only where the host has no opinion. For a library the host
/// does load, a plugin built against a newer version does not get its own copy — it gets the host's,
/// silently — and calling a member that exists only in the newer one throws
/// <c>MissingMethodException</c> at the call site. <c>BackendRegistry.ConnectAsync</c>'s catch-all turns
/// that into "Not connected", which reads as the plugin simply being unreachable — the same shape of
/// silent failure this file otherwise exists to prevent. The rule stays as it is regardless: it is the
/// one thing that keeps a mismatched SDK from producing a wrong-type cast instead of a clean rejection.
/// </para>
/// <para>
/// Nothing here is ever loaded by path, only by <see cref="LoadWithoutLocking"/> — see it for why.
/// </para>
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);

    /// <summary>
    /// Read the file and load the bytes, rather than <c>LoadFromAssemblyPath</c>, which keeps the file
    /// open for as long as the context lives.
    /// <para>
    /// These contexts are deliberately not collectible — a loaded plugin's providers outlive the scan
    /// that found them (see <c>BackendCatalog.PluginProviders</c>) — so "as long as the context lives"
    /// means until the process exits, and nothing can release the handle sooner. On Windows an open
    /// file cannot be deleted or replaced, which makes a plugin impossible to uninstall or update
    /// while Kontena is running, and made every test in <c>PluginLoaderTests</c> fail in its cleanup
    /// (KON-405). On Linux and macOS the same handle is harmless, because an open file may still be
    /// unlinked — which is why this only ever showed up on one of the three CI runners.
    /// </para>
    /// <para>
    /// The cost is a copy of the assembly on the heap instead of a memory-mapped file, and an empty
    /// <c>Assembly.Location</c> for plugin code. Both are a few hundred kilobytes' worth of nothing
    /// against a file the user cannot delete.
    /// </para>
    /// </summary>
    public Assembly LoadWithoutLocking(string path)
    {
        using var file = File.OpenRead(path);
        return LoadFromStream(file);
    }

    /// <summary>
    /// Resolve a dependency out of the plugin's own directory — unless the default context already has
    /// it, in which case return null so the runtime falls back to that one shared copy.
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsLoadedByHost(assemblyName))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadWithoutLocking(path);
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
