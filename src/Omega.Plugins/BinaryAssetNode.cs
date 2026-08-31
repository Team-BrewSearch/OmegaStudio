namespace Omega.Plugins;

/// <summary>
/// The base for a node backed by a run of bytes.
///
/// <para>
/// Implementing <see cref="IBinaryAsset"/> here rather than per plugin gives every
/// payload — a model, a texture, an unrecognised blob — raw extract and replace for
/// two overrides: where the bytes are, and how to write them back.
/// </para>
/// </summary>
public abstract class BinaryAssetNode : AssetNode, IBinaryAsset
{
    /// <param name="name">The row's label.</param>
    /// <param name="pathText">A stable, filename-safe identifier. See <see cref="IAssetNode.PathText"/>.</param>
    /// <param name="kind">Usually left alone; see <see cref="AssetKinds.Resolve"/>.</param>
    /// <param name="detail">The grey text after the label.</param>
    protected BinaryAssetNode(
        string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
        : base(name, pathText, kind, detail)
    {
    }

    /// <summary>The payload, without copying.</summary>
    public abstract ReadOnlySpan<byte> Data { get; }

    /// <summary>
    /// The payload's size. Reads <see cref="Data"/> by default; override where the
    /// size is known more cheaply than the bytes are.
    /// </summary>
    public virtual int Size => Data.Length;

    /// <summary>
    /// The extension a raw extract should suggest, including the dot. Override where
    /// the payload is a known standalone file type.
    /// </summary>
    public virtual string Extension => ".bin";

    /// <summary>Whether this node's bytes may be replaced. Override to refuse.</summary>
    public virtual bool CanReplaceBytes => true;

    /// <summary>
    /// Stores a new payload, of any length. Called by <see cref="ReplaceBytes"/> once
    /// the replacement has been checked.
    /// </summary>
    protected abstract void Write(byte[] payload);

    /// <summary>
    /// The row's grey text, recomputed whenever a replacement changes the size.
    /// Override to say more than the size — an offset, say.
    /// </summary>
    protected virtual string DescribeSize()
    {
        return Sizes.Describe(Size);
    }

    /// <summary>
    /// Swaps in a new payload of any length, then refreshes the row's detail text.
    /// Refuses when <see cref="CanReplaceBytes"/> is false.
    /// </summary>
    public void ReplaceBytes(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!CanReplaceBytes)
        {
            throw new InvalidOperationException($"'{Name}' cannot be replaced.");
        }

        Write(payload);
        Detail = DescribeSize();
    }
}
