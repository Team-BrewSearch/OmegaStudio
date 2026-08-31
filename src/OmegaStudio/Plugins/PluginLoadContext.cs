using System.Reflection;
using System.Runtime.Loader;
namespace OmegaStudio.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] s_shared = ["Omega.Plugins"];
    private readonly AssemblyDependencyResolver _resolver;
    public PluginLoadContext(string assemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && Array.Exists(s_shared, s => s == name))
        {
            return null;
        }
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
