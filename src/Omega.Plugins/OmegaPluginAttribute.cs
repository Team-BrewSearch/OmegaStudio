namespace Omega.Plugins;
/// <summary>Marks an assembly as a format plugin and names the type the loader constructs.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class OmegaPluginAttribute : Attribute
{
    /// <summary>Declares the assembly&apos;s plugin entry point.</summary>
    /// <param name="pluginType">A concrete type implementing <see cref="IFormatPlugin"/> with a parameterless constructor.</param>
    public OmegaPluginAttribute(Type pluginType)
    {
        PluginType = pluginType;
    }
    /// <summary>Type the loader instantiates to obtain the plugin.</summary>
    public Type PluginType { get; }
}
