namespace Omega.Plugins;
public abstract class BinaryAssetNode : AssetNode, IBinaryAsset
{
    protected BinaryAssetNode(
        string name, string pathText, string kind = AssetKinds.Auto, string detail = "")
        : base(name, pathText, kind, detail)
    {
    }
    public abstract ReadOnlySpan<byte> Data { get; }
    public virtual int Size
    {
        get
        {
            long known = ((IBinaryAsset)this).TryGetLength();
            return known is >= 0 and <= int.MaxValue ? (int)known : Data.Length;
        }
    }
    public virtual string Extension => ".bin";
    public virtual bool CanReplaceBytes => true;
    protected abstract void Write(byte[] payload);
    protected virtual string DescribeSize()
    {
        return Sizes.Describe(Size);
    }
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
