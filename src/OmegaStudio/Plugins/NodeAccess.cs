using Omega.Plugins;
namespace OmegaStudio.Plugins;
internal static class NodeAccess
{
    public static string Name(IAssetNode node) => Read(node, "name", static n => n.Name, string.Empty);
    public static string Detail(IAssetNode node) => Read(node, "detail", static n => n.Detail, string.Empty);
    public static string Kind(IAssetNode node) => Read(node, "kind", static n => n.Kind, string.Empty);
    public static string PathText(IAssetNode node) => Read(node, "location", static n => n.PathText, string.Empty);
    public static string FileStem(IAssetNode node) => Read(node, "file name", static n => n.FileStem, string.Empty);
    public static bool HasChildren(IAssetNode node) => Read(node, "children flag", static n => n.HasChildren, false);
    public static int Size(IBinaryAsset asset) => Read(asset, "size", static a => a.Size, 0);
    public static string Extension(IBinaryAsset asset) => Read(asset, "extension", static a => a.Extension, string.Empty);
    public static IReadOnlyList<IAssetNode> Children(IAssetNode node) =>
        Read(node, "contents", static n => (IReadOnlyList<IAssetNode>)[.. n.GetChildren()], []);
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
    private static T Read<TNode, T>(TNode node, string what, Func<TNode, T> read, T fallback)
        where TNode : class
    {
        try
        {
            return read(node);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(node, what, ex);
            return fallback;
        }
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
        PluginGuard.Report($"Reading the {what} of '{Label(node)}'", ex);
    }
}
