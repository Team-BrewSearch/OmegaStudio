using Omega.Plugins;

namespace OmegaStudio.Plugins;

/// <summary>
/// Applies the loaded <see cref="IAssetRecognizer"/>s to a plugin's nodes.
///
/// <para>
/// This is where a raw blob becomes a clip or a picture. A container plugin hands over
/// bytes it does not claim to understand; the recognisers say what they are, and the
/// node comes back wearing the capabilities that light up a pane. One place, so a WAV
/// gets a transport in every format at once.
/// </para>
/// </summary>
internal sealed class AssetRecognition
{
    private readonly List<Entry> _entries = [];

    private AssetRecognition()
    {
    }

    /// <summary>No recognisers: every node passes through untouched.</summary>
    public static AssetRecognition None { get; } = new();

    /// <summary>Collects every recogniser the loaded plugins offer, in load order.</summary>
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

    /// <summary>
    /// The node the Outliner should show: the plugin's own, or a view of it carrying
    /// the capabilities its content implies.
    ///
    /// <para>
    /// A node that already claims a content capability is left alone. The plugin knows
    /// its own format better than a magic number does, and skipping is also what lets
    /// <see cref="RecognizedAsset"/> forward only two optional interfaces rather than
    /// every one of them.
    /// </para>
    /// </summary>
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

    /// <summary>Whether any recogniser claims these bytes. Answers <see cref="IHostServices.IsKnownPayload"/>.</summary>
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

    /// <summary>
    /// The plugin's own node, behind any view wrapped around it. Used when a node's
    /// bytes change and the recognition has to be redone from scratch.
    /// </summary>
    public static IAssetNode Unwrap(IAssetNode node)
    {
        return node is RecognizedAsset recognized ? recognized.Inner : node;
    }

    /// <summary>Whether the node already says what its content is.</summary>
    private static bool HasContent(IAssetNode node)
    {
        return node is ISceneAsset or IImageAsset or IAudioAsset or IAnimatedAsset;
    }

    /// <summary>
    /// A guarded <see cref="IAssetRecognizer.Claims"/>. Not routed through
    /// <see cref="PluginGuard.Try{T}"/> because a span cannot be captured by the
    /// lambda that would need to close over it.
    /// </summary>
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

    /// <summary>A recogniser and the services its owning plugin was lent.</summary>
    private readonly record struct Entry(IAssetRecognizer Recognizer, IHostServices Host);
}
