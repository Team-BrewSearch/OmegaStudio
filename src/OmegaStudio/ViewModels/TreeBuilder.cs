using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;

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
    private IEnumerable<TreeItemViewModel> Children(IAssetNode node)
    {
        IEnumerable<IAssetNode> children = PluginGuard.Try(
            $"Reading the contents of '{node.Name}'",
            () => (IEnumerable<IAssetNode>)[.. node.GetChildren()],
            []);
        return children.Select(ForNode);
    }
}
