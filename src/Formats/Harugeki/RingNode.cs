using System.Globalization;
namespace Harugeki.Formats;
public sealed class RingNode
{
    private readonly Lock _probeLock = new();
    private List<RingNode?>? _children;
    private volatile bool _probed;
    private byte[]? _replacement;
    internal RingNode(RingArchive archive, RingNode? parent, int index, int offset, int length)
    {
        Archive = archive;
        Parent = parent;
        Index = index;
        Offset = offset;
        Length = length;
    }
    public RingArchive Archive { get; }
    public RingNode? Parent { get; }
    public int Index { get; }
    public int Offset { get; }
    public int Length { get; }
    public string PathText
    {
        get
        {
            Stack<int> stack = new();
            for (RingNode? n = this; n is { Index: >= 0 }; n = n.Parent)
            {
                stack.Push(n.Index);
            }
            return stack.Count == 0
                ? "/"
                : string.Join("-", stack.Select(i => i.ToString("00", CultureInfo.InvariantCulture)));
        }
    }
    internal object AliasKey
    {
        get => field ??= _replacement is not null ? new object() : Archive.AliasKeyFor(Offset);
        private set;
    }
    public ReadOnlySpan<byte> Span => _replacement ?? Archive.Data.AsSpan(Offset, Length);
    public int Size => _replacement?.Length ?? Length;
    public bool IsDirectory
    {
        get
        {
            EnsureProbed();
            return _children is not null;
        }
    }
    public IReadOnlyList<RingNode?> Children
    {
        get
        {
            EnsureProbed();
            return _children ?? (IReadOnlyList<RingNode?>)[];
        }
    }
    public AssetKind Kind => IsDirectory ? AssetKind.Container : AssetTypes.Detect(Span);
    public byte[] GetPayload()
    {
        return _replacement ?? Archive.Data.AsSpan(Offset, Length).ToArray();
    }
    public void Replace(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (IsDirectory)
        {
            throw new InvalidOperationException("cannot replace a container node's bytes");
        }
        _replacement = payload;
        AliasKey = new object();
        Archive.MarkDirty();
    }
    private void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }
        lock (_probeLock)
        {
            if (_probed)
            {
                return;
            }
            Probe();
            _probed = true;
        }
    }
    private void Probe()
    {
        if (_replacement is not null)
        {
            return;
        }
        if (AssetTypes.Detect(Span) != AssetKind.Unknown || Archive.Host?.IsKnownPayload(Span) == true)
        {
            return;
        }
        (int Offset, int Length)?[]? spans = RingArchive.ReadToc(Archive.Data, Offset, Length);
        if (spans is null)
        {
            return;
        }
        _children = new List<RingNode?>(spans.Length);
        for (int i = 0; i < spans.Length; i++)
        {
            _children.Add(spans[i] is { } s ? new RingNode(Archive, this, i, s.Offset, s.Length) : null);
        }
    }
    public override string ToString()
    {
        return $"[{Index:00}] 0x{Offset:X} {Length} {Kind}";
    }
}
