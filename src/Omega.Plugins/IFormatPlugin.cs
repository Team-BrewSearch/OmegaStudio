namespace Omega.Plugins;
/// <summary>Who a plugin is, as the host lists it.</summary>
public sealed record FormatPluginInfo
{
    /// <summary>Machine identifier, unique across installed plugins and stable across releases.</summary>
    public required string Id { get; init; }
    /// <summary>Name shown in menus and log lines.</summary>
    public required string DisplayName { get; init; }
    /// <summary>The plugin&apos;s own release version, independent of the contract.</summary>
    public string Version { get; init; } = "1.0.0";
    /// <summary>Whoever wrote the plugin.</summary>
    public string Author { get; init; } = string.Empty;
    /// <summary>A sentence on what the plugin reads.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Value of <see cref="PluginContract.Version"/> this assembly was compiled against.</summary>
    public int ContractVersion { get; init; } = PluginContract.Version;
}
/// <summary>The one type a plugin assembly exposes to the host.</summary>
public interface IFormatPlugin
{
    /// <summary>Identity of this plugin.</summary>
    FormatPluginInfo Info { get; }
    /// <summary>Container formats this plugin can open. Empty for a plugin that only recognises payloads.</summary>
    IEnumerable<IArchiveFormat> Archives => [];
    /// <summary>Payload recognisers this plugin contributes to every open archive, whichever plugin opened it.</summary>
    IEnumerable<IAssetRecognizer> Recognizers => [];
    /// <summary>Recognisers for containers split across sibling rows, offered every folder of every open archive.</summary>
    IEnumerable<IContainerRecognizer> Containers => [];
}
