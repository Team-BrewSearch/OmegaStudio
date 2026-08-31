using Omega.Plugins;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Turns a plugin's node tree into Outliner rows.
///
/// <para>
/// A row mirrors a node, and its children are built when it is expanded. On the way
/// through, two things happen that the plugin does not have to do for itself: the
/// node is offered to the recognisers, which may hand back a view of it that knows
/// its content is a clip or a picture; and its kind is resolved from its
/// capabilities, so a model draws blue without anyone saying so.
/// </para>
/// </summary>
internal sealed class TreeBuilder
{
    private readonly AssetRecognition _recognition;

    public TreeBuilder(AssetRecognition recognition)
    {
        _recognition = recognition;
    }

    public TreeItemViewModel ForArchive(IArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ForNode(archive.Root);
    }

    /// <summary>
    /// The view of a node its content implies, worked out afresh. Used after a
    /// replacement, when the bytes may no longer be what they were — a raw blob
    /// swapped for a WAV should gain a transport without the archive being reopened.
    /// </summary>
    public IAssetNode Recognize(IAssetNode node)
    {
        return _recognition.Enrich(AssetRecognition.Unwrap(node));
    }

    public TreeItemViewModel ForNode(IAssetNode node)
    {
        IAssetNode shown = _recognition.Enrich(node);
        TreeItemViewModel row = new(shown.Name, shown.Detail, AssetKinds.Resolve(shown)) { Node = shown };

        if (shown.HasChildren)
        {
            row.Loader = () => Children(shown);
        }

        return row;
    }

    /// <summary>
    /// A node's children as rows. Guarded because building them is the moment a plugin
    /// parses, and a blob that will not parse should cost one row rather than the whole
    /// tree.
    /// </summary>
    private IEnumerable<TreeItemViewModel> Children(IAssetNode node)
    {
        IEnumerable<IAssetNode> children = PluginGuard.Try(
            $"Reading the contents of '{node.Name}'",
            () => (IEnumerable<IAssetNode>)[.. node.GetChildren()],
            []);

        return children.Select(ForNode);
    }
}
