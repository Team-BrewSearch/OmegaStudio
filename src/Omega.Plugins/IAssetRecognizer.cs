namespace Omega.Plugins;
public interface IAssetRecognizer
{
    string Id { get; }
    bool Claims(ReadOnlySpan<byte> data);
    IAssetNode Wrap(IAssetNode node, IHostServices host);
}
public abstract class RecognizedAsset : IAssetNode, IBinaryAsset, IDescribable, ICommandSource
{
    private readonly IBinaryAsset _bytes;
    protected RecognizedAsset(IAssetNode inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
        _bytes = inner as IBinaryAsset
            ?? throw new ArgumentException("a recognised node must hold bytes", nameof(inner));
    }
    public IAssetNode Inner { get; }
    public string Name => Inner.Name;
    public string Detail => Inner.Detail;
    public string PathText => Inner.PathText;
    public virtual string FileStem => Inner.FileStem;
    public virtual string Kind => Inner.Kind;
    public bool HasChildren => Inner.HasChildren;
    public IEnumerable<IAssetNode> GetChildren()
    {
        return Inner.GetChildren();
    }
    public int Size => _bytes.Size;
    public ReadOnlySpan<byte> Data => _bytes.Data;
    public ReadOnlySpan<byte> Head => _bytes.Head;
    public long TryGetLength() => _bytes.TryGetLength();
    public virtual string Extension => _bytes.Extension;
    public bool CanReplaceBytes => _bytes.CanReplaceBytes;
    public void ReplaceBytes(byte[] payload)
    {
        _bytes.ReplaceBytes(payload);
    }
    public virtual IEnumerable<PropertyRow> Describe()
    {
        return Inner is IDescribable describable ? describable.Describe() : [];
    }
    public virtual IEnumerable<AssetCommand> GetCommands()
    {
        return Inner is ICommandSource source ? source.GetCommands() : [];
    }
    public override string ToString()
    {
        return Inner.ToString() ?? Name;
    }
}
