using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal sealed class TreeBuilder
{
    private readonly AssetRecognition _recognition;
    private readonly Action<string, int, long> _built;
    public TreeBuilder(AssetRecognition recognition, Action<string>? log = null)
    {
        _recognition = recognition;
        _built = (name, count, ms) => log?.Invoke($"Expanded '{name}': {count} row(s) in {ms} ms.");
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
        IReadOnlyList<IAssetNode> children = [.. NodeAccess.Children(node).Select(_recognition.Enrich)];
        return _recognition.Group(children).Select(Row);
    }
    private TreeItemViewModel Row(IAssetNode shown)
    {
        TreeItemViewModel row = new(NodeAccess.Name(shown), NodeAccess.Detail(shown), NodeAccess.Resolve(shown))
        {
            Node = shown,
            OnBuilt = _built,
        };
        if (NodeAccess.HasChildren(shown))
        {
            row.Loader = () => Children(shown);
        }
        return row;
    }
}
