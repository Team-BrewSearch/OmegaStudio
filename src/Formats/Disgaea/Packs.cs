using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal abstract class Pack
{
    public abstract string Format { get; }
    public abstract int Count { get; }
    public abstract string NameOf(int index);
    public abstract ReadOnlySpan<byte> Get(int index);
    public abstract void Set(int index, byte[] payload);
    public abstract byte[] Build();
}
internal sealed class Dsarc : Pack
{
    private const int HeaderSize = 16;
    private const int EntrySize = 48;
    private const int NameSize = 40;
    private const int Alignment = 512;
    private readonly byte[] _header;
    private readonly int[] _entryAt;
    private readonly string[] _names;
    private readonly int[] _blockOf;
    private readonly byte[][] _blocks;
    private Dsarc(bool indexed, byte[] header, int[] entryAt, string[] names, int[] blockOf, byte[][] blocks)
    {
        Format = indexed ? "DSARCIDX" : "DSARC FL";
        _header = header;
        _entryAt = entryAt;
        _names = names;
        _blockOf = blockOf;
        _blocks = blocks;
    }
    public override string Format { get; }
    public override int Count => _names.Length;
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("DSARC FL"u8) || data.StartsWith("DSARCIDX"u8);
    }
    public static Dsarc Read(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, HeaderSize);
        bool indexed = data.StartsWith("DSARCIDX"u8);
        if (!indexed && !data.StartsWith("DSARC FL"u8))
        {
            throw new InvalidDataException("not a DSARC archive");
        }
        uint count = BinaryRead.U32(data, 8);
        uint version = BinaryRead.U32(data, 12);
        if (!indexed && version != 1)
        {
            throw new InvalidDataException($"DSARC FL version {version} is not 1");
        }
        long table = indexed ? (HeaderSize + (2L * count) + 3) & ~3L : HeaderSize;
        long tableEnd = table + (EntrySize * (long)count);
        if (tableEnd > data.Length)
        {
            throw new InvalidDataException($"{count} entries do not fit a {data.Length}-byte archive");
        }
        int[] entryAt = new int[count];
        string[] names = new string[count];
        (int Offset, int Size)[] spans = new (int, int)[count];
        int first = (int)tableEnd;
        for (int i = 0; i < count; i++)
        {
            int at = (int)(table + (EntrySize * (long)i));
            uint size = BinaryRead.U32(data, at + NameSize);
            uint offset = BinaryRead.U32(data, at + NameSize + 4);
            if (offset < tableEnd || offset + (ulong)size > (ulong)data.Length)
            {
                throw new InvalidDataException($"entry {i} at 0x{offset:X} (+{size}) lies outside the archive");
            }
            entryAt[i] = at;
            string name = BinaryRead.Name(data, at, NameSize);
            names[i] = name.Length > 0 ? name : $"file_{i}";
            spans[i] = ((int)offset, (int)size);
            first = i == 0 ? (int)offset : Math.Min(first, (int)offset);
        }
        (int Offset, int Size)[] distinct = [.. spans.Distinct().OrderBy(s => s.Offset).ThenBy(s => s.Size)];
        Dictionary<(int, int), int> index = [];
        byte[][] blocks = new byte[distinct.Length][];
        for (int i = 0; i < distinct.Length; i++)
        {
            index[distinct[i]] = i;
            blocks[i] = data.Slice(distinct[i].Offset, distinct[i].Size).ToArray();
        }
        int[] blockOf = [.. spans.Select(s => index[s])];
        return new Dsarc(indexed, data[..first].ToArray(), entryAt, names, blockOf, blocks);
    }
    public override string NameOf(int index)
    {
        return _names[index];
    }
    public override ReadOnlySpan<byte> Get(int index)
    {
        return _blocks[_blockOf[index]];
    }
    public override void Set(int index, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _blocks[_blockOf[index]] = payload;
    }
    public override byte[] Build()
    {
        int first = _header.Length;
        long[] offsets = new long[_blocks.Length];
        long end = first;
        for (int i = 0; i < _blocks.Length; i++)
        {
            offsets[i] = first + AlignUp(end - first);
            end = offsets[i] + _blocks[i].Length;
        }
        long total = first + AlignUp(end - first);
        if (total > int.MaxValue)
        {
            throw new InvalidDataException($"the rebuilt {Format} archive would need {total} bytes");
        }
        byte[] output = new byte[total];
        _header.CopyTo(output, 0);
        for (int i = 0; i < _blocks.Length; i++)
        {
            _blocks[i].CopyTo(output, offsets[i]);
        }
        for (int i = 0; i < _entryAt.Length; i++)
        {
            int block = _blockOf[i];
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(_entryAt[i] + NameSize), _blocks[block].Length);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(_entryAt[i] + NameSize + 4), (int)offsets[block]);
        }
        return output;
    }
    private static long AlignUp(long value)
    {
        return (value + Alignment - 1) / Alignment * Alignment;
    }
}
internal sealed class Dseq : Pack
{
    private const int HeaderSize = 48;
    private const int Alignment = 32;
    private static readonly string[] s_extensions = [".sseq", ".sbnk", ".swar"];
    private readonly byte[] _header;
    private readonly string _stem;
    private readonly byte[][] _parts;
    private Dseq(byte[] header, string stem, byte[][] parts)
    {
        _header = header;
        _stem = stem;
        _parts = parts;
    }
    public override string Format => "DSEQ";
    public override int Count => _parts.Length;
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("DSEQ"u8);
    }
    public static Dseq Read(ReadOnlySpan<byte> data, string name)
    {
        BinaryRead.Need(data, 0, HeaderSize);
        if (!Claims(data))
        {
            throw new InvalidDataException("not a DSEQ file");
        }
        long sseq = BinaryRead.U32(data, 0x10) - 16L;
        long[] offsets = [sseq, BinaryRead.U32(data, 0x14), BinaryRead.U32(data, 0x18)];
        byte[][] parts = new byte[3][];
        for (int i = 0; i < 3; i++)
        {
            uint size = BinaryRead.U32(data, 0x20 + (4 * i));
            if (offsets[i] < HeaderSize || offsets[i] + size > data.Length)
            {
                throw new InvalidDataException($"the {s_extensions[i][1..].ToUpperInvariant()} chunk lies outside the file");
            }
            parts[i] = data.Slice((int)offsets[i], (int)size).ToArray();
        }
        return new Dseq(data[..HeaderSize].ToArray(), Path.GetFileNameWithoutExtension(name), parts);
    }
    public override string NameOf(int index)
    {
        return _stem + s_extensions[index];
    }
    public override ReadOnlySpan<byte> Get(int index)
    {
        return _parts[index];
    }
    public int Revision { get; private set; }
    public override void Set(int index, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _parts[index] = payload;
        Revision++;
    }
    public override byte[] Build()
    {
        long sbnk = AlignUp(HeaderSize + _parts[0].Length + 16L);
        long swar = AlignUp(sbnk + _parts[1].Length);
        long[] offsets = [HeaderSize, sbnk, swar];
        long total = swar + _parts[2].Length;
        if (total > int.MaxValue)
        {
            throw new InvalidDataException($"the rebuilt DSEQ would need {total} bytes");
        }
        byte[] output = new byte[total];
        _header.CopyTo(output, 0);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x10 + (4 * i)), (int)offsets[i] + (i == 0 ? 16 : 0));
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x20 + (4 * i)), _parts[i].Length);
            _parts[i].CopyTo(output, offsets[i]);
        }
        return output;
    }
    private static long AlignUp(long value)
    {
        return (value + Alignment - 1) / Alignment * Alignment;
    }
}
internal sealed class Lz10Pack : Pack
{
    private readonly string _name;
    private byte[] _data;
    private Lz10Pack(string name, byte[] data)
    {
        _name = name;
        _data = data;
    }
    public override string Format => "LZ10";
    public override int Count => 1;
    public static Lz10Pack Read(ReadOnlySpan<byte> data, string name)
    {
        string inner = Path.GetExtension(name).Equals(".mpds", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(name, ".mpd")
            : Path.GetFileNameWithoutExtension(name) + ".bin";
        return new Lz10Pack(inner, Lz10.Decompress(data));
    }
    public override string NameOf(int index)
    {
        return _name;
    }
    public override ReadOnlySpan<byte> Get(int index)
    {
        return _data;
    }
    public override void Set(int index, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _data = payload;
    }
    public override byte[] Build()
    {
        return Lz10.Compress(_data);
    }
}
internal sealed class Swar : Pack
{
    private const int HeaderSize = 0x10;
    private const int TableAt = 0x3C;
    private const int SwavHeader = 0x18;
    private const int InfoSize = 0x0C;
    private readonly byte[] _header;
    private readonly string _stem;
    private readonly byte[][] _waves;
    private Swar(byte[] header, string stem, byte[][] waves)
    {
        _header = header;
        _stem = stem;
        _waves = waves;
    }
    public override string Format => "SWAR";
    public override int Count => _waves.Length;
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("SWAR"u8);
    }
    public static Swar Read(ReadOnlySpan<byte> data, string name)
    {
        BinaryRead.Need(data, 0, TableAt);
        if (!Claims(data) || !data.Slice(HeaderSize, 4).SequenceEqual("DATA"u8))
        {
            throw new InvalidDataException("not a SWAR wave archive");
        }
        uint count = BinaryRead.U32(data, 0x38);
        long tableEnd = TableAt + (4L * count);
        if (tableEnd > data.Length)
        {
            throw new InvalidDataException($"{count} waves do not fit a {data.Length}-byte SWAR");
        }
        byte[][] waves = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            uint offset = BinaryRead.U32(data, TableAt + (4 * i));
            if (offset < tableEnd || offset > data.Length - InfoSize)
            {
                throw new InvalidDataException($"wave {i} at 0x{offset:X} lies outside the SWAR");
            }
            waves[i] = data.Slice((int)offset, Length(data[(int)offset..], $"wave {i}")).ToArray();
        }
        return new Swar(data[..TableAt].ToArray(), Path.GetFileNameWithoutExtension(name), waves);
    }
    public override string NameOf(int index)
    {
        return $"{_stem}_{index:D3}.swav";
    }
    public override ReadOnlySpan<byte> Get(int index)
    {
        byte[] wave = _waves[index];
        byte[] swav = new byte[SwavHeader + wave.Length];
        "SWAV"u8.CopyTo(swav);
        _header.AsSpan(4, 4).CopyTo(swav.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(swav.AsSpan(8), swav.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(swav.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(swav.AsSpan(14), 1);
        "DATA"u8.CopyTo(swav.AsSpan(HeaderSize));
        BinaryPrimitives.WriteInt32LittleEndian(swav.AsSpan(0x14), swav.Length - HeaderSize);
        wave.CopyTo(swav, SwavHeader);
        return swav;
    }
    public override void Set(int index, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.AsSpan().StartsWith("SWAV"u8) || payload.Length < SwavHeader + InfoSize)
        {
            throw new InvalidDataException("a SWAR entry must be replaced with a SWAV file");
        }
        ReadOnlySpan<byte> wave = payload.AsSpan(SwavHeader);
        _waves[index] = wave[..Length(wave, "the SWAV")].ToArray();
    }
    public override byte[] Build()
    {
        long total = TableAt + (4L * _waves.Length) + _waves.Sum(w => (long)w.Length);
        if (total > int.MaxValue)
        {
            throw new InvalidDataException($"the rebuilt SWAR would need {total} bytes");
        }
        byte[] output = new byte[total];
        _header.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x08), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x14), output.Length - HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x38), _waves.Length);
        int at = TableAt + (4 * _waves.Length);
        for (int i = 0; i < _waves.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(TableAt + (4 * i)), at);
            _waves[i].CopyTo(output, at);
            at += _waves[i].Length;
        }
        return output;
    }
    private static int Length(ReadOnlySpan<byte> wave, string what)
    {
        long length = InfoSize + ((BinaryRead.U16(wave, 6) + (long)BinaryRead.U32(wave, 8)) * 4);
        return length <= wave.Length ? (int)length
            : throw new InvalidDataException($"{what} claims {length} bytes but only {wave.Length} remain");
    }
}
