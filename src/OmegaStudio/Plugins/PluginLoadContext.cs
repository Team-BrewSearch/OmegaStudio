using System.Reflection;
using System.Runtime.Loader;
namespace OmegaStudio.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string EmbeddedPrefix = "omega/deps/";
    private static readonly string[] s_shared = ["Omega.Plugins"];
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _path;
    private Assembly? _plugin;
    public PluginLoadContext(string assemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        _path = assemblyPath;
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }
    public Assembly LoadPlugin()
    {
        return _plugin ??= LoadFromAssemblyPath(_path);
    }
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name || Array.Exists(s_shared, s => s == name))
        {
            return null;
        }
        if (Embedded(name) is { } embedded)
        {
            return embedded;
        }
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    private Assembly? Embedded(string name)
    {
        if (_plugin is not { } plugin)
        {
            return null;
        }
        using Stream? library = plugin.GetManifestResourceStream(EmbeddedPrefix + name + ".dll");
        return library is null ? null : LoadFromStream(library);
    }
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
