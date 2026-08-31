namespace Omega.Plugins;

/// <summary>
/// Names the <see cref="IFormatPlugin"/> an assembly provides. One line, in any
/// source file of the plugin:
///
/// <code>
/// [assembly: OmegaPlugin(typeof(MyPlugin))]
/// </code>
///
/// <para>
/// The host reads this attribute instead of scanning every type in the assembly
/// for one implementing an interface. That is faster, but mostly it is
/// predictable: registration is a thing the author wrote down, not a thing the
/// host guessed, so a plugin that fails to load fails for a reason that can be
/// printed.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class OmegaPluginAttribute : Attribute
{
    /// <param name="pluginType">
    /// A concrete type implementing <see cref="IFormatPlugin"/> with a public
    /// parameterless constructor.
    /// </param>
    public OmegaPluginAttribute(Type pluginType)
    {
        PluginType = pluginType;
    }

    /// <summary>The type the host will instantiate.</summary>
    public Type PluginType { get; }
}
