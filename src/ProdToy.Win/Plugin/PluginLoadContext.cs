using System.Reflection;
using System.Runtime.Loader;

namespace ProdToy;

/// <summary>
/// Custom AssemblyLoadContext for plugin isolation.
/// Each plugin gets its own collectible context so it can be unloaded.
/// ProdToy.Sdk is resolved from the default context to ensure shared type identity.
/// </summary>
sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginDllPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // ProdToy.Sdk must come from the default context so host and plugin
        // share the same IPlugin, IPluginHost, etc. type identity.
        if (assemblyName.Name == "ProdToy.Sdk")
            return null;

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
            return LoadFromAssemblyPath(assemblyPath);

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
            return LoadUnmanagedDllFromPath(libraryPath);

        return IntPtr.Zero;
    }
}
