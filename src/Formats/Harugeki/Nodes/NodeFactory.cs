using Omega.Plugins;
using System.Diagnostics;
namespace Harugeki.Formats.Nodes;

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
    public static IEnumerable<IAssetNode> Children(RingNode node)
    {
        IReadOnlyList<RingNode?> children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
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
