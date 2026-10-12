namespace Omega.Plugins;
public interface IPayloadSource
{
    long Length { get; }
    int Head(Span<byte> destination);
    byte[] ReadAll();
}
public abstract class StreamingAssetNode : BinaryAssetNode, IBinaryAsset
{
    public const int HeadWindow = 4096;
    private readonly IPayloadSource _source;
    private byte[]? _cache;
    private byte[]? _head;
    protected StreamingAssetNode(
        string name,
        string pathText,
        IPayloadSource source,
        string kind = AssetKinds.Auto,
        string detail = "")
        : base(name, pathText, kind, detail)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }
    public sealed override int Size => (int)Math.Min(int.MaxValue, _source.Length);
    public long TryGetLength() => _source.Length;
    public override ReadOnlySpan<byte> Data
    {
        get
        {
            return _cache ??= _source.ReadAll();
        }
    }
    ReadOnlySpan<byte> IBinaryAsset.Head
    {
        get
        {
            byte[]? cached = _cache;
            if (cached is not null)
            {
                return cached.Length <= HeadWindow ? cached : cached.AsSpan(0, HeadWindow);
            }
            if (_head is not null)
            {
                return _head.Length <= HeadWindow ? _head : _head.AsSpan(0, HeadWindow);
            }
            byte[] head = new byte[(int)Math.Min(HeadWindow, _source.Length)];
            int read = _source.Head(head);
            _head = read == head.Length ? head : head[..read];
            return _head.AsSpan();
        }
    }
    protected IPayloadSource Source => _source;
    public void ReleasePayload()
    {
        _cache = null;
    }
    public void ReleaseAll()
    {
        _cache = null;
        _head = null;
    }
}
