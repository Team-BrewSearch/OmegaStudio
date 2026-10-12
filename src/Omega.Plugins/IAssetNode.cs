namespace Omega.Plugins;
public interface IAssetNode
{
    string Name { get; }
    string Detail { get; }
    string PathText { get; }
    string FileStem { get; }
    string Kind { get; }
    bool HasChildren { get; }
    IEnumerable<IAssetNode> GetChildren();
}
