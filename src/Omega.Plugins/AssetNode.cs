namespace Omega.Plugins;

public abstract class AssetNode : IAssetNode
{
    protected AssetNode(string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
    {
        Name = name ?? string.Empty;
        PathText = pathText ?? string.Empty;
        Kind = kind ?? AssetKinds.Auto;
        Detail = detail ?? string.Empty;
    }
    public string Name { get; }
    public string PathText { get; }
    public string Detail { get; protected set; }
    public string Kind { get; protected set; }
    protected Func<IEnumerable<IAssetNode>>? ChildFactory { get; set; }
    public virtual bool HasChildren => ChildFactory is not null;
    public virtual IEnumerable<IAssetNode> GetChildren()
    {
        return ChildFactory?.Invoke() ?? [];
    }
    public override string ToString()
    {
        return string.IsNullOrEmpty(Detail) ? Name : $"{Name}  {Detail}";
    }
}
public sealed class GroupNode : AssetNode
{
    public GroupNode(
        string name,
        string pathText,
        string detail,
        Func<IEnumerable<IAssetNode>> children,
        string kind = AssetKinds.Container)
        : base(name, pathText, kind, detail)
    {
        ArgumentNullException.ThrowIfNull(children);
        ChildFactory = children;
    }
}
