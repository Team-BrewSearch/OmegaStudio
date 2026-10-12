using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace Polarbit.Formats;
internal sealed class VfsArchive
{
    private const uint SizeFlag = 0x80000000;
    private readonly List<VfsEntry> _entries;
    private readonly Dictionary<uint, VfsEntry> _byHash;
    private readonly byte[]? _nameTail;
    private readonly int _nameBlockLength;
    private int _savedRevision;
    private VfsArchive(List<VfsEntry> entries, byte[]? nameTail, int nameBlockLength)
    {
        _nameBlockLength = nameBlockLength;
        _entries = entries;
        _nameTail = nameTail;
        _byHash = entries.ToDictionary(e => e.Hash);
        foreach (VfsEntry entry in entries)
        {
            entry.Owner = this;
        }
    }
    public IReadOnlyList<VfsEntry> Entries => _entries;
    public bool HasNameTable => _nameTail is not null;
    public int Revision { get; private set; }
    public bool IsDirty => Revision != _savedRevision;
    public event Action? Changed;
    internal void MarkChanged()
    {
        Revision++;
        Changed?.Invoke();
    }
    public void MarkSaved()
    {
        _savedRevision = Revision;
    }
    public VfsEntry? Find(uint hash)
    {
        return _byHash.GetValueOrDefault(hash);
    }
    public VfsEntry? Find(string path)
    {
        return Find(NameHash(path));
    }
    public static uint NameHash(string path)
    {
        uint h = 0;
        foreach (char c in path)
        {
            char upper = char.ToUpperInvariant(c);
            h = unchecked((h * 67) + (byte)(upper <= 0xFF ? upper : c) - 113);
        }
        return h;
    }
    public static bool LooksLikeArchive(ReadOnlySpan<byte> head, long length)
    {
        if (head.Length < 12 || !head.StartsWith("FUFS"u8))
        {
            return false;
        }
        uint tag = Le.U32(head, 4);
        long count = Le.U32(head, 8);
        return (tag & SizeFlag) != 0 && (tag & ~SizeFlag) <= length && 12 + (count * 12) <= length;
    }
    public static VfsArchive Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Le.Need(LooksLikeArchive(data, data.Length), "Not a FUFS archive: bad magic, size tag or entry count.");
        int count = (int)Le.U32(data, 8);
        int tableEnd = 12 + (count * 12);
        List<VfsEntry> entries = new(count);
        long payloadEnd = tableEnd;
        bool sorted = true;
        for (int i = 0; i < count; i++)
        {
            int at = 12 + (i * 12);
            uint offset = Le.U32(data, at);
            uint hash = Le.U32(data, at + 4);
            uint size = Le.U32(data, at + 8);
            if (offset < tableEnd || (long)offset + size > data.Length)
            {
                throw new InvalidDataException(
                    $"Entry {i} (hash {hash:X8}) claims {size} bytes at 0x{offset:X}, outside the {data.Length}-byte file.");
            }
            if (i > 0 && hash <= entries[^1].Hash)
            {
                sorted = false;
            }
            entries.Add(new VfsEntry(hash, data.AsMemory((int)offset, (int)size)));
            payloadEnd = System.Math.Max(payloadEnd, offset + size);
        }
        Le.Need(entries.Select(e => e.Hash).Distinct().Count() == count, "Two entries share one name hash.");
        long end = Le.U32(data, 4) & ~SizeFlag;
        byte[]? tail = null;
        int blockLength = 0;
        if (end != data.Length && end > payloadEnd && end + 4 <= data.Length)
        {
            int start = (int)payloadEnd;
            blockLength = (int)(end - start);
            Le.Need(Le.U32(data, (int)end) == blockLength && data[(int)end - 1] == 0,
                $"The name table at 0x{start:X} does not match its length word.");
            string[] names = Encoding.Latin1.GetString(data, start, blockLength - 1).Split('\0');
            Le.Need(names.Length == count, $"The name table holds {names.Length} names for {count} entries.");
            for (int i = 0; i < count; i++)
            {
                if (NameHash(names[i]) == entries[i].Hash)
                {
                    entries[i].Name = names[i];
                }
            }
            tail = data[start..];
        }
        if (!sorted)
        {
            entries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            (tail, blockLength) = tail is null ? (null, 0) : BuildNameTail(entries);
        }
        return new VfsArchive(entries, tail, blockLength);
    }
    public byte[] Build()
    {
        int count = _entries.Count;
        long total = 12L + (count * 12L) + _entries.Sum(e => (long)e.RawSize) + (_nameTail?.Length ?? 0);
        Le.Need(total < int.MaxValue, "The rebuilt archive would exceed 2 GB.");
        byte[] output = new byte[total];
        "FUFS"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)count);
        int position = 12 + (count * 12);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> raw = _entries[i].Raw;
            Span<byte> row = output.AsSpan(12 + (i * 12));
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)position);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], _entries[i].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)raw.Length);
            raw.CopyTo(output.AsSpan(position));
            position += raw.Length;
        }
        int end = position;
        if (_nameTail is not null)
        {
            _nameTail.CopyTo(output.AsSpan(position));
            end += _nameBlockLength;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), SizeFlag | (uint)end);
        return output;
    }
    private static (byte[] Tail, int BlockLength) BuildNameTail(List<VfsEntry> entries)
    {
        byte[] block = Encoding.Latin1.GetBytes(string.Concat(entries.Select(e => (e.Name ?? string.Empty) + "\0")));
        using MemoryStream copy = new();
        using (ZLibStream z = new(copy, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(block);
            z.Flush();
            byte[] flushed = copy.ToArray();
            byte[] tail = new byte[block.Length + 4 + flushed.Length];
            block.CopyTo(tail, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(block.Length), (uint)block.Length);
            flushed.CopyTo(tail, block.Length + 4);
            return (tail, block.Length);
        }
    }
}
internal readonly record struct Layer(bool Plzp, int Level)
{
    public override string ToString()
    {
        return $"{(Plzp ? "PLZP" : "zlib")} (level {Level})";
    }
}
internal sealed class VfsEntry
{
    private readonly ReadOnlyMemory<byte> _source;
    private byte[]? _raw;
    private byte[]? _payload;
    private Layer[]? _layers;
    private PayloadKind? _kind;
    private int _sourcePayloadLength = -1;
    public VfsEntry(uint hash, ReadOnlyMemory<byte> source)
    {
        Hash = hash;
        _source = source;
    }
    internal VfsArchive? Owner { get; set; }
    public uint Hash { get; }
    public string? Name { get; set; }
    public bool NameRecovered { get; set; }
    public ReadOnlySpan<byte> Raw => _raw is null ? _source.Span : _raw;
    public int RawSize => _raw?.Length ?? _source.Length;
    public IReadOnlyList<Layer> Layers
    {
        get
        {
            EnsureLoaded();
            return _layers!;
        }
    }
    public byte[] Payload
    {
        get
        {
            EnsureLoaded();
            return _payload!;
        }
    }
    public PayloadKind Kind => _kind ??= Kinds.Sniff(Payload);
    public void Replace(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureLoaded();
        _raw = payload.Length == _sourcePayloadLength && Unwrap(_source.Span).Payload.AsSpan().SequenceEqual(payload)
            ? null
            : Wrap(payload, _layers!);
        _payload = payload;
        _kind = null;
        Owner?.MarkChanged();
    }
    private void EnsureLoaded()
    {
        if (_payload is null)
        {
            (Layer[] layers, byte[] payload) = Unwrap(_source.Span);
            _layers = layers;
            _payload = payload;
            _sourcePayloadLength = payload.Length;
        }
    }
    private static (Layer[] Layers, byte[] Payload) Unwrap(ReadOnlySpan<byte> blob)
    {
        List<Layer> layers = [];
        byte[] current = blob.ToArray();
        while (true)
        {
            if (current.Length >= 12 && current.AsSpan().StartsWith("PLZP"u8))
            {
                uint rawSize = Le.U32(current, 4);
                uint compressed = Le.U32(current, 8);
                Le.Need(compressed == current.Length - 12 && rawSize <= Zlib.MaxInflated,
                    $"PLZP header claims {compressed} compressed and {rawSize} raw bytes in a {current.Length}-byte entry.");
                byte[] inner = Zlib.Inflate(current.AsSpan(12), (int)rawSize);
                layers.Add(new Layer(true, Level(current[13])));
                current = inner;
            }
            else if (IsZlib(current) && Zlib.TryInflate(current) is { } inner)
            {
                layers.Add(new Layer(false, Level(current[1])));
                current = inner;
            }
            else
            {
                return ([.. layers], current);
            }
        }
    }
    private static bool IsZlib(ReadOnlySpan<byte> b)
    {
        return b.Length > 2 && b[0] == 0x78 && ((b[0] << 8) | b[1]) % 31 == 0;
    }
    private static int Level(byte flags)
    {
        return (flags >> 6) switch
        {
            0 => 1,
            1 => 3,
            2 => 6,
            _ => 9,
        };
    }
    private static byte[] Wrap(byte[] payload, Layer[] layers)
    {
        byte[] current = payload;
        for (int i = layers.Length - 1; i >= 0; i--)
        {
            byte[] z = Zlib.Deflate(current, layers[i].Level);
            if (layers[i].Plzp)
            {
                byte[] wrapped = new byte[12 + z.Length];
                "PLZP"u8.CopyTo(wrapped);
                BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4), (uint)current.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(8), (uint)z.Length);
                z.CopyTo(wrapped, 12);
                z = wrapped;
            }
            current = z;
        }
        return current;
    }
}
internal static class Zlib
{
    public const int MaxInflated = 256 << 20;
    public static byte[] Inflate(ReadOnlySpan<byte> z, int size)
    {
        byte[] output = new byte[size];
        using ZLibStream stream = new(new MemoryStream(z.ToArray()), CompressionMode.Decompress);
        stream.ReadExactly(output);
        Le.Need(stream.ReadByte() < 0, $"zlib stream inflates past its declared {size} bytes.");
        return output;
    }
    public static byte[]? TryInflate(byte[] z)
    {
        try
        {
            using ZLibStream stream = new(new MemoryStream(z), CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                Le.Need(output.Length <= MaxInflated, "zlib stream inflates past 256 MB.");
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
    public static byte[] Deflate(ReadOnlySpan<byte> data, int level)
    {
        using MemoryStream output = new();
        using (ZLibStream z = new(output, level >= 7 ? CompressionLevel.SmallestSize
            : level <= 2 ? CompressionLevel.Fastest : CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }
        return output.ToArray();
    }
}
