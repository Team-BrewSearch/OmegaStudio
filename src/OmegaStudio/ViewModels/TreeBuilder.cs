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
        return Row(_recognition.Enrich(node));
    }
    public IEnumerable<TreeItemViewModel> Children(IAssetNode node)
    {
        IReadOnlyList<IAssetNode> children = PluginGuard.Try(
            $"Reading the contents of '{node.Name}'",
            () => (IReadOnlyList<IAssetNode>)[.. node.GetChildren()],
            []);
        children = [.. children.Select(_recognition.Enrich)];
        return _recognition.Group(children).Select(Row);
    }
    private TreeItemViewModel Row(IAssetNode shown)
    {
        TreeItemViewModel row = new(shown.Name, shown.Detail, AssetKinds.Resolve(shown)) { Node = shown };
        if (shown.HasChildren)
        {
            row.Loader = () => Children(shown);
        }
        return row;
    }
}
