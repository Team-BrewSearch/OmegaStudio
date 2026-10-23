using Omega.Plugins;
namespace OmegaStudio.Plugins;
internal sealed class AssetRecognition
{
    private readonly List<Entry> _entries = [];
    private readonly List<ContainerEntry> _containers = [];
    private AssetRecognition()
    {
    }
    public static AssetRecognition None { get; } = new();
    public static AssetRecognition From(PluginCatalog catalog, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(log);
        AssetRecognition recognition = new();
        foreach (LoadedPlugin plugin in catalog.Plugins)
        {
            IReadOnlyList<IAssetRecognizer> recognizers = PluginGuard.Try(
                $"Listing recognisers for {plugin.Info.DisplayName}",
                () => (IReadOnlyList<IAssetRecognizer>)[.. plugin.Plugin.Recognizers],
                []);
            foreach (IAssetRecognizer recognizer in recognizers)
            {
                recognition._entries.Add(
                    new Entry(recognizer, new HostServices(plugin, log, recognition)));
            }
            IReadOnlyList<IContainerRecognizer> containers = PluginGuard.Try(
                $"Listing container recognisers for {plugin.Info.DisplayName}",
                () => (IReadOnlyList<IContainerRecognizer>)[.. plugin.Plugin.Containers],
                []);
            foreach (IContainerRecognizer container in containers)
            {
                recognition._containers.Add(
                    new ContainerEntry(container, new HostServices(plugin, log, recognition)));
            }
        }
        return recognition;
    }
    public int Count => _entries.Count;
    public IAssetNode Enrich(IAssetNode node)
    {
        if (_entries.Count == 0 || node is not IBinaryAsset bytes || HasContent(node))
        {
            return node;
        }
        ReadOnlySpan<byte> head = Head(node);
        foreach (Entry entry in _entries)
        {
            if (!Claims(entry.Recognizer, head))
            {
                continue;
            }
            IAssetNode? wrapped = PluginGuard.Try<IAssetNode?>(
                $"Recognising '{NodeAccess.Name(node)}' as {entry.Recognizer.Id}",
                () => entry.Recognizer.Wrap(node, entry.Host),
                null);
            if (wrapped is not null)
            {
                return wrapped;
            }
        }
        return node;
    }
    private static ReadOnlySpan<byte> Head(IAssetNode node)
    {
        if (node is not IBinaryAsset bytes)
        {
            return [];
        }
        try
        {
            return bytes.Head;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginGuard.Log?.Invoke(
                $"Reading the head of '{NodeAccess.Name(node)}' failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }
    public IReadOnlyList<IAssetNode> Group(IReadOnlyList<IAssetNode> children)
    {
        foreach (ContainerEntry entry in _containers)
        {
            IReadOnlyList<IAssetNode>? claimed = PluginGuard.Try<IReadOnlyList<IAssetNode>?>(
                $"Looking for {entry.Recognizer.Id} containers",
                () => entry.Recognizer.Claim(children, entry.Host),
                null);
            if (claimed is null)
            {
                continue;
            }
            if (claimed.Any(row => row is null))
            {
                PluginGuard.Log?.Invoke(
                    $"Container recogniser {entry.Recognizer.Id} returned an empty row; its claim was ignored.");
                continue;
            }
            children = claimed;
        }
        return children;
    }
    public bool IsKnownPayload(ReadOnlySpan<byte> data)
    {
        foreach (Entry entry in _entries)
        {
            if (Claims(entry.Recognizer, data))
            {
                return true;
            }
        }
        return false;
    }
    public static IAssetNode Unwrap(IAssetNode node)
    {
        return node is RecognizedAsset recognized ? recognized.Inner : node;
    }
    private static bool HasContent(IAssetNode node)
    {
        return node is ISceneAsset or IImageAsset or IAudioAsset or IVideoAsset or IAnimatedAsset;
    }
    private static bool Claims(IAssetRecognizer recognizer, ReadOnlySpan<byte> data)
    {
        try
        {
            return recognizer.Claims(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginGuard.Log?.Invoke($"Recogniser {recognizer.Id} failed while sniffing: {ex.Message}");
            return false;
        }
    }
    private readonly record struct Entry(IAssetRecognizer Recognizer, IHostServices Host);
    private readonly record struct ContainerEntry(IContainerRecognizer Recognizer, IHostServices Host);
}
