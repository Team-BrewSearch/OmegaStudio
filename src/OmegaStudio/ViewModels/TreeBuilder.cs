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
    public TreeItemViewModel ForArchive(IArchive archive, TreeItemViewModel.Runner runner)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(runner);
        return Row(_recognition.Enrich(archive.Root), runner);
    }
    public IAssetNode Recognize(IAssetNode node)
    {
        return _recognition.Enrich(AssetRecognition.Unwrap(node));
    }
    public IEnumerable<TreeItemViewModel> Children(IAssetNode node, TreeItemViewModel.Runner? runner)
    {
        IReadOnlyList<IAssetNode> children = [.. NodeAccess.Children(node).Select(_recognition.Enrich)];
        return _recognition.Group(children).Select(child => Row(child, runner));
    }
    private TreeItemViewModel Row(IAssetNode shown, TreeItemViewModel.Runner? runner)
    {
        TreeItemViewModel row = new(NodeAccess.Name(shown), NodeAccess.Detail(shown), NodeAccess.Resolve(shown))
        {
            Node = shown,
            OnBuilt = _built,
            Run = runner,
        };
        if (NodeAccess.HasChildren(shown))
        {
            row.Loader = () => Children(shown, runner);
        }
        return row;
    }
}
