namespace Omega.Plugins;
/// <summary>One row in the asset tree. The capability interfaces decide what the row can do.</summary>
public interface IAssetNode
{
    /// <summary>Label shown in the tree.</summary>
    string Name { get; }
    /// <summary>Secondary text beside the name, usually a size or a count.</summary>
    string Detail { get; }
    /// <summary>Slash-separated location of this row inside its archive.</summary>
    string PathText { get; }
    /// <summary>The row&apos;s own name with no extension and none of the decoration <see cref="Name"/> carries, used to suggest a filename when the row is extracted or exported.</summary>
    string FileStem { get; }
    /// <summary>One of the strings on <see cref="AssetKinds"/>, or empty to let the host infer it.</summary>
    string Kind { get; }
    /// <summary>Whether the row can be expanded. Answered without parsing the children.</summary>
    bool HasChildren { get; }
    /// <summary>Produces the child rows. Called only when the row is first expanded.</summary>
    /// <returns>The children, in display order.</returns>
    IEnumerable<IAssetNode> GetChildren();
}
