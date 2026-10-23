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
            byte[]? cached = Cached;
            if (cached is not null)
            {
                PayloadCache.Touch(this);
                return cached;
            }
            byte[] payload = _source.ReadAll();
            PayloadCache.Keep(this, payload);
            return payload;
        }
    }
    ReadOnlySpan<byte> IBinaryAsset.Head
    {
        get
        {
            byte[]? cached = Cached;
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
    internal byte[]? Cached { get; set; }
    internal LinkedListNode<PayloadCache.Entry>? Kept { get; set; }
    public void ReleasePayload()
    {
        PayloadCache.Release(this);
    }
    [Obsolete("Use ReleasePayload; the head window is small and is kept. This will be removed at the next contract version.")]
    public void ReleaseAll()
    {
        PayloadCache.Release(this);
        _head = null;
    }
}
