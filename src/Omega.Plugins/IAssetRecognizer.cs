namespace Omega.Plugins;
/// <summary>Claims blobs by content and wraps them in a richer node, whichever plugin owns the container they came from.</summary>
public interface IAssetRecognizer
{
    /// <summary>Machine identifier for this recogniser.</summary>
    string Id { get; }
    /// <summary>Whether these bytes are the payload this recogniser handles.</summary>
    /// <param name="data">The blob to test, from its first byte.</param>
    /// <returns>True to claim the blob.</returns>
    bool Claims(ReadOnlySpan<byte> data);
    /// <summary>Wraps a claimed node in one that decodes the payload.</summary>
    /// <param name="node">The plain node holding the bytes.</param>
    /// <param name="host">Services the wrapper may call.</param>
    /// <returns>The wrapping node, which the tree shows in place of the original.</returns>
    IAssetNode Wrap(IAssetNode node, IHostServices host);
}
/// <summary>Base for a wrapper node: forwards identity and bytes to the node it wraps, and adds whatever the recognised format knows.</summary>
public abstract class RecognizedAsset : IAssetNode, IBinaryAsset, IDescribable, ICommandSource
{
    private readonly IBinaryAsset _bytes;
    /// <summary>Wraps a node that must already hold bytes.</summary>
    /// <param name="inner">The node being wrapped.</param>
    protected RecognizedAsset(IAssetNode inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
        _bytes = inner as IBinaryAsset
            ?? throw new ArgumentException("a recognised node must hold bytes", nameof(inner));
    }
    /// <summary>The node this one wraps.</summary>
    public IAssetNode Inner { get; }
    /// <summary>Label shown in the tree, from the wrapped node.</summary>
    public string Name => Inner.Name;
    /// <summary>Secondary text beside the name, from the wrapped node.</summary>
    public string Detail => Inner.Detail;
    /// <summary>Slash-separated location, from the wrapped node.</summary>
    public string PathText => Inner.PathText;
    /// <summary>Name to build an exported filename from, from the wrapped node.</summary>
    public virtual string FileStem => Inner.FileStem;
    /// <summary>The row&apos;s kind. Override to advertise what the payload turned out to be.</summary>
    public virtual string Kind => Inner.Kind;
    /// <summary>Whether the wrapped node has children.</summary>
    public bool HasChildren => Inner.HasChildren;
    /// <summary>The wrapped node&apos;s children.</summary>
    /// <returns>The children, in display order.</returns>
    public IEnumerable<IAssetNode> GetChildren()
    {
        return Inner.GetChildren();
    }
    /// <summary>Length of the payload in bytes.</summary>
    public int Size => _bytes.Size;
    /// <summary>The payload as it stands now.</summary>
    public ReadOnlySpan<byte> Data => _bytes.Data;
    /// <summary>Extension to suggest when extracting. Override once the format is known.</summary>
    public virtual string Extension => _bytes.Extension;
    /// <summary>Whether the wrapped node accepts a byte replacement.</summary>
    public bool CanReplaceBytes => _bytes.CanReplaceBytes;
    /// <summary>Replaces the wrapped node&apos;s payload.</summary>
    /// <param name="payload">The new bytes.</param>
    public void ReplaceBytes(byte[] payload)
    {
        _bytes.ReplaceBytes(payload);
    }
    /// <summary>Property rows for this node. Override to add what the decoded payload reveals.</summary>
    /// <returns>The rows, in display order.</returns>
    public virtual IEnumerable<PropertyRow> Describe()
    {
        return Inner is IDescribable describable ? describable.Describe() : [];
    }
    /// <summary>Context-menu entries for this node. Override to add what the decoded payload allows.</summary>
    /// <returns>The commands, in menu order.</returns>
    public virtual IEnumerable<AssetCommand> GetCommands()
    {
        return Inner is ICommandSource source ? source.GetCommands() : [];
    }
    /// <summary>The wrapped node&apos;s own description.</summary>
    public override string ToString()
    {
        return Inner.ToString() ?? Name;
    }
}
