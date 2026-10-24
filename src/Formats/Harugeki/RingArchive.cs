using System.Buffers.Binary;
using Omega.Plugins;
namespace Harugeki.Formats;
public sealed class RingArchive
{
    public const byte Pad = 0xFE;
    public const int Align = 32;
    public static long MaxArchiveSize => Array.MaxLength;
    private const int MinArchiveSize = 8;
    internal const int MaxTableCount = 8192;
    private readonly Lock _aliasLock = new();
    private readonly Dictionary<int, object> _aliasKeys = [];
    public RingArchive(byte[] data, string? path = null, IHostServices? host = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Path = path;
        Host = host;
        Root = new RingNode(this, null, -1, 0, data.Length);
    }
    public byte[] Data { get; }
    public string? Path { get; }
    public RingNode Root { get; }
    internal IHostServices? Host { get; }
    public bool IsDirty { get; private set; }
    public int Revision { get; private set; }
    internal void MarkDirty()
    {
        IsDirty = true;
        Revision++;
    }
    public void MarkSaved()
    {
        IsDirty = false;
    }
    public static RingArchive Load(string path, IHostServices? host = null)
    {
        FileInfo file = new(path);
        return file.Length > MaxArchiveSize
            ? throw new InvalidOperationException(
                $"{System.IO.Path.GetFileName(path)} is {Sizes.Describe(file.Length)} and this reader holds the whole "
                + $"image in memory, so {Sizes.Describe(MaxArchiveSize)} is as much as one open can be. Split it, or "
                + "browse it with a tool that streams.")
            : file.Length < MinArchiveSize
            ? throw new InvalidOperationException("Archive file is too small to be valid")
            : new RingArchive(File.ReadAllBytes(path), path, host);
    }
    internal object AliasKeyFor(int offset)
    {
        lock (_aliasLock)
        {
            if (!_aliasKeys.TryGetValue(offset, out object? key))
            {
                _aliasKeys[offset] = key = new object();
            }
            return key;
        }
    }
    internal static int AlignUp(int value)
    {
        return (value + Align - 1) & ~(Align - 1);
    }
    public static bool LooksLikeArchive(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head.Length < MinArchiveSize || fileLength < MinArchiveSize)
        {
            return false;
        }
        int count = (int)BinaryRead.U32(head, 0);
        if (count is <= 0 or >= MaxTableCount)
        {
            return false;
        }
        int need = 4 + (count * 4);
        if (need > head.Length)
        {
            return false;
        }
        int first = int.MaxValue;
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            uint value = BinaryRead.U32(head, 4 + (i * 4));
            if (value > fileLength)
            {
                return false;
            }
            if (value == 0)
            {
                continue;
            }
            if (value < (uint)need)
            {
                return false;
            }
            any = true;
            first = System.Math.Min(first, (int)value);
        }
        if (!any)
        {
            return false;
        }
        for (int i = need; i < System.Math.Min(first, head.Length); i++)
        {
            if (head[i] != Pad)
            {
                return false;
            }
        }
        return true;
    }
    internal static (int Offset, int Length)?[]? ReadToc(ReadOnlySpan<byte> data, int baseOff, int size)
    {
        if (size < MinArchiveSize)
        {
            return null;
        }
        ReadOnlySpan<byte> span = data.Slice(baseOff, size);
        int count = (int)BinaryRead.U32(span, 0);
        if (count is <= 0 or >= MaxTableCount)
        {
            return null;
        }
        int need = 4 + (count * 4);
        if (need > size)
        {
            return null;
        }
        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint value = BinaryRead.U32(span, 4 + (i * 4));
            if (value > int.MaxValue)
            {
                return null;
            }
            offsets[i] = (int)value;
        }
        int[] distinct = [.. offsets.Where(o => o != 0).Distinct().Order()];
        if (distinct.Length == 0 || distinct[0] < need || distinct[^1] > size)
        {
            return null;
        }
        for (int i = need; i < distinct[0]; i++)
        {
            if (span[i] != Pad)
            {
                return null;
            }
        }
        Dictionary<int, int> endOf = new(distinct.Length);
        for (int i = 0; i < distinct.Length; i++)
        {
            endOf[distinct[i]] = i + 1 < distinct.Length ? distinct[i + 1] : size;
        }
        (int, int)?[] spans = new (int, int)?[count];
        for (int i = 0; i < count; i++)
        {
            spans[i] = offsets[i] == 0 ? null : (baseOff + offsets[i], endOf[offsets[i]] - offsets[i]);
        }
        return spans;
    }
    public byte[] Save()
    {
        return Serialize(Root);
    }
    private static byte[] Serialize(RingNode node)
    {
        if (!node.IsDirectory)
        {
            return node.GetPayload();
        }
        IReadOnlyList<RingNode?> children = node.Children;
        int count = children.Count;
        int need = 4 + (count * 4);
        int first = AlignUp(need);
        using MemoryStream body = new();
        int[] offsets = new int[count];
        Dictionary<object, int> seen = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < count; i++)
        {
            RingNode? child = children[i];
            if (child is null)
            {
                offsets[i] = 0;
                continue;
            }
            object key = child.AliasKey;
            if (seen.TryGetValue(key, out int already))
            {
                offsets[i] = already;
                continue;
            }
            int at = first + (int)body.Length;
            byte[] data = Serialize(child);
            body.Write(data, 0, data.Length);
            int padTo = AlignUp((int)body.Length);
            for (int p = (int)body.Length; p < padTo; p++)
            {
                body.WriteByte(0);
            }
            offsets[i] = at;
            seen[key] = at;
        }
        byte[] result = new byte[first + (int)body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4 + (i * 4)), (uint)offsets[i]);
        }
        result.AsSpan(need, first - need).Fill(Pad);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(first));
        return result;
    }
}
