using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Omega.Plugins;
using SMath = System.Math;
namespace Virtuos.Formats;
public sealed class DwkArchive
{
    private readonly List<DwkEntry> _entries;
    private readonly int _alignment;
    private DwkArchive(List<DwkEntry> entries, int alignment)
    {
        _entries = entries;
        _alignment = alignment;
        foreach (DwkEntry entry in entries)
        {
            entry.Owner = this;
        }
    }
    public IReadOnlyList<DwkEntry> Entries => _entries;
    public bool HasChanges => _entries.Exists(e => e.IsDirty);
    public int Revision { get; private set; }
    public event Action? Changed;
    internal void MarkChanged()
    {
        Revision++;
        Changed?.Invoke();
    }
    public static DwkArchive Load(string gdaPath, string gdfPath, string gdtPath)
    {
        byte[] gdaData = File.ReadAllBytes(gdaPath);
        byte[] gdfData = File.ReadAllBytes(gdfPath);
        byte[] gdtData = File.ReadAllBytes(gdtPath);
        return Build(gdaData, gdfData, gdtData);
    }
    public static DwkArchive Load(byte[] gdaData, byte[] gdfData, byte[] gdtData)
    {
        ArgumentNullException.ThrowIfNull(gdaData);
        ArgumentNullException.ThrowIfNull(gdfData);
        ArgumentNullException.ThrowIfNull(gdtData);
        return Build(gdaData, gdfData, gdtData);
    }
    private static DwkArchive Build(byte[] gdaData, byte[] gdfData, byte[] gdtData)
    {
        if (gdfData.Length < 4 || gdtData.Length < 8)
        {
            throw new InvalidDataException("The GDF or GDT is too small to hold its own header.");
        }
        uint gdfCount = BinaryRead.U32(gdfData, 0);
        long nameTableStart = 4L + ((long)gdfCount * 4);
        if (nameTableStart > gdfData.Length)
        {
            throw new InvalidDataException(
                $"The GDF declares {gdfCount} entries, more than its {gdfData.Length} bytes can hold.");
        }
        List<string> paths = new((int)gdfCount);
        for (int i = 0; i < gdfCount; i++)
        {
            uint relOff = BinaryRead.U32(gdfData, 4 + (i * 4));
            long absOff = nameTableStart + relOff;
            if (absOff >= gdfData.Length)
            {
                paths.Add(string.Empty);
                continue;
            }
            int start = (int)absOff;
            int end = start;
            while (end < gdfData.Length && gdfData[end] != 0)
            {
                end++;
            }
            paths.Add(Encoding.ASCII.GetString(gdfData, start, end - start));
        }
        long declared = BinaryRead.U32(gdtData, 4);
        long fits = SMath.Max(0, (gdtData.Length - 8L) / 8);
        int effective = (int)SMath.Min(SMath.Min(declared, fits), paths.Count);
        List<DwkEntry> entries = new(effective);
        List<uint> offsetsSeen = new(effective);
        for (int i = 0; i < effective; i++)
        {
            uint offset = BinaryRead.U32(gdtData, 8 + (i * 8));
            uint size = BinaryRead.U32(gdtData, 8 + (i * 8) + 4);
            offsetsSeen.Add(offset);
            bool valid = size > 0 && (long)offset + size <= gdaData.Length;
            Memory<byte> mem = valid ? gdaData.AsMemory((int)offset, (int)size) : Memory<byte>.Empty;
            entries.Add(new DwkEntry(i, paths[i], mem, valid ? (int)offset : -1));
        }
        int alignment = DetectAlignment(offsetsSeen);
        return new DwkArchive(entries, alignment);
    }
    private static int DetectAlignment(List<uint> offsets)
    {
        uint[] nonZero = [.. offsets.Where(o => o > 0)];
        if (nonZero.Length == 0)
        {
            return 32;
        }
        for (int candidate = 4096; candidate >= 4; candidate /= 2)
        {
            if (Array.TrueForAll(nonZero, o => o % candidate == 0))
            {
                return candidate;
            }
        }
        return 4;
    }
    public (byte[] Gda, byte[] Gdt) Pack()
    {
        int count = _entries.Count;
        byte[] gdtBytes = new byte[8 + (count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(0), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(4), (uint)count);
        Dictionary<Hash256, List<(int Offset, byte[] Data)>> written = [];
        using MemoryStream gdaStream = new();
        for (int i = 0; i < count; i++)
        {
            byte[] data = _entries[i].DataForSave;
            int offset;
            if (data.Length == 0)
            {
                offset = (int)gdaStream.Length;
            }
            else
            {
                Hash256 hash = Hash256.Of(data);
                offset = FindOrWrite(written, hash, data, gdaStream);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(8 + (i * 8)), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(8 + (i * 8) + 4), (uint)data.Length);
        }
        byte[] gda = gdaStream.ToArray();
        VerifyRepack(_entries, gda, gdtBytes);
        return (gda, gdtBytes);
    }
    private int FindOrWrite(
        Dictionary<Hash256, List<(int Offset, byte[] Data)>> written, Hash256 hash, byte[] data, MemoryStream gdaStream)
    {
        if (written.TryGetValue(hash, out List<(int Offset, byte[] Data)>? candidates))
        {
            foreach ((int candidateOffset, byte[] candidateData) in candidates)
            {
                if (candidateData.AsSpan().SequenceEqual(data))
                {
                    return candidateOffset;
                }
            }
        }
        else
        {
            written[hash] = candidates = [];
        }
        int offset = (int)gdaStream.Length;
        gdaStream.Write(data);
        int aligned = AlignUp((int)gdaStream.Length, _alignment);
        while (gdaStream.Length < aligned)
        {
            gdaStream.WriteByte(0);
        }
        candidates.Add((offset, data));
        return offset;
    }
    private readonly record struct Hash256(long A, long B, long C, long D)
    {
        public static Hash256 Of(byte[] data)
        {
            Span<byte> digest = stackalloc byte[32];
            _ = SHA256.HashData(data, digest);
            return new Hash256(
                BinaryPrimitives.ReadInt64LittleEndian(digest),
                BinaryPrimitives.ReadInt64LittleEndian(digest[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(digest[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(digest[24..]));
        }
    }
    private static void VerifyRepack(List<DwkEntry> entries, byte[] gda, byte[] gdt)
    {
        uint count = BinaryRead.U32(gdt, 4);
        if (count != entries.Count)
        {
            throw new InvalidDataException($"Repack produced {count} GDT entries, expected {entries.Count}.");
        }
        for (int i = 0; i < entries.Count; i++)
        {
            uint offset = BinaryRead.U32(gdt, 8 + (i * 8));
            uint size = BinaryRead.U32(gdt, 8 + (i * 8) + 4);
            byte[] expected = entries[i].DataForSave;
            if (size != expected.Length || offset + size > gda.Length
                || !gda.AsSpan((int)offset, (int)size).SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"Repack verification failed for entry {i} ({entries[i].GdfPath}): " +
                    $"wrote {size} B at {offset}, expected {expected.Length} B to match source bytes exactly.");
            }
        }
    }
    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }
}
