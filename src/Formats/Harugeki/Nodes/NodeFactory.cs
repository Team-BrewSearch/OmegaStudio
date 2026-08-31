using Omega.Plugins;
using System.Diagnostics;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// Decides what kind of node a slot in the archive becomes.
///
/// <para>
/// One switch on what the sniffer found. A blob that claims to be a model but will
/// not parse falls back to a raw node rather than throwing: the archive is full of
/// slots, and one unreadable payload should cost its own row and nothing else.
/// </para>
/// </summary>
internal static class NodeFactory
{
    public static IAssetNode For(RingNode node)
    {
        string header = $"[{node.Index:00}]";

        return node.Kind switch
        {
            AssetKind.Container => new ContainerNode(node, header),
            AssetKind.Model => TryBuild(() => new ModelNode(node, header), node, header),
            AssetKind.Texture => TryBuild(() => new TextureNode(node, header), node, header),
            AssetKind.Animation => TryBuild(() => new AnimationNode(node, header), node, header),
            _ => new RawNode(node, header),
        };
    }

    /// <summary>Every child slot of a container, with empty slots kept as placeholders.</summary>
    public static IEnumerable<IAssetNode> Children(RingNode node)
    {
        IReadOnlyList<RingNode?> children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            // An empty slot is a real feature of the format — an offset of zero — and
            // showing it keeps the row indices lined up with the file's own numbering.
            yield return children[i] is { } child
                ? For(child)
                : new EmptySlotNode(node, i);
        }
    }

    private static IAssetNode TryBuild(Func<IAssetNode> build, RingNode node, string header)
    {
        try
        {
            return build();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"[Harugeki] slot {node.PathText} looked parseable but was not: {ex.Message}");
            return new RawNode(node, header);
        }
    }
}
