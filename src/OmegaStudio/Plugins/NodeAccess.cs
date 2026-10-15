using Omega.Plugins;
namespace OmegaStudio.Plugins;
internal static class NodeAccess
{
    public static string Name(IAssetNode node) =>
        PluginGuard.Try($"Reading the name of '{Label(node)}'", () => node.Name, string.Empty);
    public static string Detail(IAssetNode node) =>
        PluginGuard.Try($"Reading the detail of '{Label(node)}'", () => node.Detail, string.Empty);
    public static string Kind(IAssetNode node) =>
        PluginGuard.Try($"Reading the kind of '{Label(node)}'", () => node.Kind, string.Empty);
    public static string PathText(IAssetNode node) =>
        PluginGuard.Try($"Reading the location of '{Label(node)}'", () => node.PathText, string.Empty);
    public static string FileStem(IAssetNode node) =>
        PluginGuard.Try($"Reading the file name of '{Label(node)}'", () => node.FileStem, string.Empty);
    public static bool HasChildren(IAssetNode node) =>
        PluginGuard.Try($"Asking '{Label(node)}' whether it has children", () => node.HasChildren, false);
    public static ReadOnlySpan<byte> Data(IBinaryAsset asset)
    {
        try
        {
            return asset.Data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(asset, "payload", ex);
            return [];
        }
    }
    public static int Size(IBinaryAsset asset) =>
        PluginGuard.Try($"Reading the size of '{Label(asset)}'", () => asset.Size, 0);
    public static string Extension(IBinaryAsset asset) =>
        PluginGuard.Try($"Reading the extension of '{Label(asset)}'", () => asset.Extension, string.Empty);
    public static ReadOnlySpan<byte> EncodedAudio(IAudioAsset asset)
    {
        try
        {
            return asset.EncodedAudio;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(asset, "audio", ex);
            return [];
        }
    }
    public static string Resolve(IAssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string declared = Kind(node);
        return !string.IsNullOrEmpty(declared)
            ? declared
            : node switch
            {
                ISceneAsset => AssetKinds.Model,
                IImageAsset => AssetKinds.Texture,
                IVideoAsset => AssetKinds.Video,
                IAudioAsset => AssetKinds.Audio,
                _ => HasChildren(node) ? AssetKinds.Container : AssetKinds.Unknown,
            };
    }
    private static string Label(object node)
    {
        return PluginGuard.Try(
            "Reading a row's label",
            () => (node as IAssetNode)?.ToString() ?? node.GetType().Name,
            node.GetType().Name);
    }
    private static void Report(object node, string what, Exception ex)
    {
        PluginGuard.Log?.Invoke(
            $"Reading the {what} of '{Label(node)}' failed: {ex.GetType().Name}: {ex.Message}");
    }
}
