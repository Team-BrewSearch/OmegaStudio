namespace Omega.Plugins;
/// <summary>Base for tree rows: fixed identity, children built on demand.</summary>
public abstract class AssetNode : IAssetNode
{
    /// <summary>Initialises the row.</summary>
    /// <param name="name">Label shown in the tree.</param>
    /// <param name="pathText">Slash-separated location inside the archive.</param>
    /// <param name="kind">One of the strings on <see cref="AssetKinds"/>, or empty to let the host infer it.</param>
    /// <param name="detail">Secondary text beside the name.</param>
    protected AssetNode(string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
    {
        Name = name ?? string.Empty;
        PathText = pathText ?? string.Empty;
        Kind = kind ?? AssetKinds.Auto;
        Detail = detail ?? string.Empty;
    }
    /// <summary>Label shown in the tree.</summary>
    public string Name { get; }
    /// <summary>Slash-separated location inside the archive.</summary>
    public string PathText { get; }
    /// <summary>Name to build an extracted or exported filename from. Defaults to <see cref="Name"/>; override where the label carries decoration the file should not.</summary>
    public virtual string FileStem => Name;
    /// <summary>Secondary text beside the name. Subclasses update this after an edit.</summary>
    public string Detail { get; protected set; }
    /// <summary>The row&apos;s kind, or empty for the host to infer.</summary>
    public string Kind { get; protected set; }
    /// <summary>Supplies the children, or null for a leaf. Setting it makes the row expandable.</summary>
    protected Func<IEnumerable<IAssetNode>>? ChildFactory { get; set; }
    /// <summary>Whether the row can be expanded.</summary>
    public virtual bool HasChildren => ChildFactory is not null;
    /// <summary>Builds the child rows.</summary>
    /// <returns>The children, or none for a leaf.</returns>
    public virtual IEnumerable<IAssetNode> GetChildren()
    {
        return ChildFactory?.Invoke() ?? [];
    }
    /// <summary>The name with its detail, for diagnostics.</summary>
    public override string ToString()
    {
        return string.IsNullOrEmpty(Detail) ? Name : $"{Name}  {Detail}";
    }
}
/// <summary>A row that exists only to hold other rows.</summary>
public sealed class GroupNode : AssetNode
{
    /// <summary>Creates a folder row.</summary>
    /// <param name="name">Label shown in the tree.</param>
    /// <param name="pathText">Slash-separated location inside the archive.</param>
    /// <param name="detail">Secondary text beside the name, usually a child count.</param>
    /// <param name="children">Supplies the child rows when the row is expanded.</param>
    /// <param name="kind">The row&apos;s kind.</param>
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
