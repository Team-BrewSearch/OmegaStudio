namespace Omega.Plugins;

/// <summary>
/// The base every node derives from: it carries the row's text and the lazy child
/// plumbing, leaving a subclass to add only the capabilities it actually has.
///
/// <para>
/// A leaf needs nothing beyond the constructor. A node with children sets
/// <see cref="ChildFactory"/>, which the host calls once, on first expand.
/// </para>
/// </summary>
public abstract class AssetNode : IAssetNode
{
    /// <param name="name">The row's label.</param>
    /// <param name="pathText">A stable, filename-safe identifier. See <see cref="IAssetNode.PathText"/>.</param>
    /// <param name="kind">
    /// One of <see cref="AssetKinds"/>, or any string of your own. Left alone it is
    /// derived from the node's capabilities — see <see cref="AssetKinds.Resolve"/>.
    /// </param>
    /// <param name="detail">The grey text after the label.</param>
    protected AssetNode(string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
    {
        Name = name ?? string.Empty;
        PathText = pathText ?? string.Empty;
        Kind = kind ?? AssetKinds.Auto;
        Detail = detail ?? string.Empty;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string PathText { get; }

    /// <summary>
    /// Settable by a subclass, because replacing a payload can change the row's
    /// detail — a resized texture, a grown blob — without the row being rebuilt.
    /// </summary>
    public string Detail { get; protected set; }

    /// <inheritdoc/>
    public string Kind { get; protected set; }

    /// <summary>
    /// How to build this node's children, or <see langword="null"/> for a leaf.
    /// Assigning it is what makes <see cref="HasChildren"/> true.
    /// </summary>
    protected Func<IEnumerable<IAssetNode>>? ChildFactory { get; set; }

    /// <inheritdoc/>
    public virtual bool HasChildren => ChildFactory is not null;

    /// <inheritdoc/>
    public virtual IEnumerable<IAssetNode> GetChildren()
    {
        return ChildFactory?.Invoke() ?? [];
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return string.IsNullOrEmpty(Detail) ? Name : $"{Name}  {Detail}";
    }
}

/// <summary>
/// A plain grouping row that owns no bytes — the "Meshes" or "Textures" header a
/// model hangs its parts off, or a virtual folder in a path-based archive.
/// </summary>
public sealed class GroupNode : AssetNode
{
    /// <inheritdoc/>
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
