namespace Omega.Plugins;
/// <summary>Base for a row whose payload is bytes the host can extract, and usually replace.</summary>
public abstract class BinaryAssetNode : AssetNode, IBinaryAsset
{
    /// <summary>Initialises the row.</summary>
    /// <param name="name">Label shown in the tree.</param>
    /// <param name="pathText">Slash-separated location inside the archive.</param>
    /// <param name="kind">One of the strings on <see cref="AssetKinds"/>, or empty to let the host infer it.</param>
    /// <param name="detail">Secondary text beside the name.</param>
    protected BinaryAssetNode(
        string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
        : base(name, pathText, kind, detail)
    {
    }
    /// <summary>The payload as it stands now.</summary>
    public abstract ReadOnlySpan<byte> Data { get; }
    /// <summary>Length of <see cref="Data"/> in bytes.</summary>
    public virtual int Size => Data.Length;
    /// <summary>Extension to suggest when extracting.</summary>
    public virtual string Extension => ".bin";
    /// <summary>Whether this row accepts a wholesale byte replacement.</summary>
    public virtual bool CanReplaceBytes => true;
    /// <summary>Stores a replacement payload. Reached only once the guards in <see cref="ReplaceBytes"/> have passed.</summary>
    /// <param name="payload">The new bytes.</param>
    protected abstract void Write(byte[] payload);
    /// <summary>The detail text to show once the payload has changed.</summary>
    /// <returns>The new detail text.</returns>
    protected virtual string DescribeSize()
    {
        return Sizes.Describe(Size);
    }
    /// <summary>Replaces the payload and refreshes the row&apos;s detail text.</summary>
    /// <param name="payload">The new bytes; the node takes ownership of the array.</param>
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
