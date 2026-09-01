namespace Omega.Plugins;
/// <summary>Generation of the plugin API, so the host can refuse an assembly built against a different one.</summary>
public static class PluginContract
{
    /// <summary>Generation the host and the plugins shipped beside it currently speak.</summary>
    public const int Version = 4;
}
