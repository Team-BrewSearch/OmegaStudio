using System.Reflection;
using System.Runtime.Loader;

namespace OmegaStudio.Plugins;

/// <summary>
/// The load context one format plugin lives in.
///
/// <para>
/// Each plugin gets its own, so two plugins may ship different versions of the same
/// dependency without one of them silently getting the other's. What they must
/// <i>not</i> get their own copy of is the contract: if a plugin loaded its own
/// <c>Omega.Plugins.dll</c>, its <c>IFormatPlugin</c> would be a different type from
/// the host's despite the identical name, and every cast across the seam would fail
/// with a message that blames the wrong thing entirely. So anything the host already
/// has is deliberately resolved from the default context instead — that is what
/// returning null from <see cref="Load"/> means.
/// </para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies that must always come from the host, whatever a plugin ships.
    ///
    /// <para>
    /// The build already keeps <c>Omega.Plugins.dll</c> out of a plugin's output —
    /// see <c>src/Plugin.props</c> — so this is the second line of defence, for a
    /// plugin built by hand or by a project that got the reference wrong. Failing
    /// here with a working app beats failing later with an inexplicable cast.
    /// </para>
    /// </summary>
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
