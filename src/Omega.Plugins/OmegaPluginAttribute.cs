namespace Omega.Plugins;
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class OmegaPluginAttribute : Attribute
{
    public OmegaPluginAttribute(Type pluginType)
    {
        PluginType = pluginType;
    }
    public Type PluginType { get; }
}
