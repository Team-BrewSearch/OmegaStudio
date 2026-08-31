namespace Omega.Plugins;

/// <summary>
/// One row in the Outliner, and the unit everything else in the host hangs off.
///
/// <para>
/// A node on its own is just a label. What it can <i>do</i> is expressed by which of
/// the capability interfaces in <c>Capabilities.cs</c> it also implements: the host
/// tests each with <c>is</c> and lights up exactly the panes and menu items those
/// imply. That is the whole extension mechanism — nothing needs registering.
/// </para>
///
/// <para>
/// Derive from <see cref="AssetNode"/> rather than implementing this directly, or
/// <see cref="BinaryAssetNode"/> if the node owns bytes.
/// </para>
/// </summary>
public interface IAssetNode
{
    /// <summary>The row's label.</summary>
    string Name { get; }

    /// <summary>Grey text after the label: a size, a texture's dimensions, a frame count.</summary>
    string Detail { get; }

    /// <summary>
    /// A stable identifier within the archive, e.g. <c>00-08-02</c> or
    /// <c>tracks/berk/main</c>. Names extracted files, so it must be filename-safe and
    /// survive a reload.
    /// </summary>
    string PathText { get; }

    /// <summary>
    /// The row's colour dot. Leave it empty and <see cref="AssetKinds.Resolve"/>
    /// derives one from the capabilities, which is usually what you want.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Whether this node has children, answered <i>without</i> building them so the
    /// expander arrow can be drawn before anything is parsed.
    /// </summary>
    bool HasChildren { get; }

    /// <summary>
    /// Builds this node's children, at most once, on first expand — which is what
    /// keeps a 133 MB archive instant to open. Do the parsing here, not in the
    /// constructor.
    ///
    /// <para>
    /// Throwing is contained: the host shows the message as a single child row and
    /// leaves the rest of the tree usable.
    /// </para>
    /// </summary>
    IEnumerable<IAssetNode> GetChildren();
}
