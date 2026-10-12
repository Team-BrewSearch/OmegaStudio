namespace Omega.Plugins;
public sealed record FormatPluginInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int ContractVersion { get; init; } = PluginContract.Version;
}
public interface IFormatPlugin
{
    FormatPluginInfo Info { get; }
    IEnumerable<IArchiveFormat> Archives => [];
    IEnumerable<IAssetRecognizer> Recognizers => [];
    IEnumerable<IContainerRecognizer> Containers => [];
}
