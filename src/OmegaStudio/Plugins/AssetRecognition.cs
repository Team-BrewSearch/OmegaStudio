using Omega.Plugins;
namespace OmegaStudio.Plugins;

internal sealed class AssetRecognition
{
    private readonly List<Entry> _entries = [];
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
        foreach (Entry entry in _entries)
        {
            if (!Claims(entry.Recognizer, bytes.Data))
            {
                continue;
            }
            IAssetNode? wrapped = PluginGuard.Try<IAssetNode?>(
                $"Recognising '{node.Name}' as {entry.Recognizer.Id}",
                () => entry.Recognizer.Wrap(node, entry.Host),
                null);
            if (wrapped is not null)
            {
                return wrapped;
            }
        }
        return node;
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
        return node is ISceneAsset or IImageAsset or IAudioAsset or IAnimatedAsset;
    }
    private static bool Claims(IAssetRecognizer recognizer, ReadOnlySpan<byte> data)
    {
        try
        {
            return recognizer.Claims(data);
        }
        catch (Exception ex)
        {
            PluginGuard.Log?.Invoke($"Recogniser {recognizer.Id} failed while sniffing: {ex.Message}");
            return false;
        }
    }
    private readonly record struct Entry(IAssetRecognizer Recognizer, IHostServices Host);
}
