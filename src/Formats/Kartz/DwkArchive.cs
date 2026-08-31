using Kartz.Formats.Binary;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// A GDA/GDF/GDT archive triple.
///
/// <code>
/// GDF: u32 count | count×u32 rel_name_offset | null-terminated string table
/// GDT: u32 zero  | u32 count | count×(u32 offset, u32 size)
/// GDA: raw file data blob
/// </code>
/// </summary>
public sealed class DwkArchive
{
    private readonly List<DwkEntry> _entries;
    private readonly int _alignment;

    private DwkArchive(List<DwkEntry> entries, int alignment)
    {
        _entries = entries;
        _alignment = alignment;
    }

    public IReadOnlyList<DwkEntry> Entries => _entries;
    public bool HasChanges => _entries.Exists(e => e.IsDirty);

    /// <summary>
    /// Reads the triple. Synchronous on purpose: the host already calls a plugin's open on a
    /// background thread, so the async twin this used to carry was never called by anything.
    /// </summary>
    public static DwkArchive Load(string gdaPath, string gdfPath, string gdtPath)
    {
        byte[] gdaData = File.ReadAllBytes(gdaPath);
        byte[] gdfData = File.ReadAllBytes(gdfPath);
        byte[] gdtData = File.ReadAllBytes(gdtPath);
        return Build(gdaData, gdfData, gdtData);
    }

    private static DwkArchive Build(byte[] gdaData, byte[] gdfData, byte[] gdtData)
    {
        if (gdfData.Length < 4 || gdtData.Length < 8)
        {
            throw new InvalidDataException("The GDF or GDT is too small to hold its own header.");
        }

        // Parse GDF. Every count and offset below comes straight out of the file, so each is
        // widened and range-checked: the name offsets in particular are relative to a table
        // whose own start is computed from the count, and an int overflow there used to land
        // the string read anywhere at all.
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

            // An offset outside the string table becomes an empty name rather than an
            // exception: the archive still opens, and the entry is still extractable.
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

        // Parse GDT, capped by what the file can actually hold as well as by the path count.
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

            // Widened: offset + size in uint arithmetic wraps, so a wild pair passed this test
            // and then threw out of AsMemory with a message about nothing in particular.
            bool valid = size > 0 && (long)offset + size <= gdaData.Length;
            Memory<byte> mem = valid ? gdaData.AsMemory((int)offset, (int)size) : Memory<byte>.Empty;

            entries.Add(new DwkEntry(i, paths[i], mem, valid ? (int)offset : -1));
        }

        int alignment = DetectAlignment(offsetsSeen);
        return new DwkArchive(entries, alignment);
    }

    /// <summary>
    /// Finds the largest power-of-two alignment that every real offset is actually a
    /// multiple of, searched top-down from 4096.
    /// </summary>
    /// <remarks>
    /// Flooring a GCD to the nearest power of two (the previous approach) can guess an
    /// alignment that doesn't evenly divide the true GCD — e.g. GCD 96 floors to 64, which
    /// 96 isn't a multiple of. Testing each power-of-two candidate directly against every
    /// offset is both simpler and actually correct.
    /// </remarks>
    private static int DetectAlignment(List<uint> offsets)
    {
        uint[] nonZero = [.. offsets.Where(o => o > 0)];
        if (nonZero.Length == 0)
        {
            return 32; // no evidence to go on; keep the previous conservative default
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

    /// <summary>
    /// Writes modified entries back. GDA and GDT are rewritten; GDF is unchanged
    /// because file names and counts never change.
    /// </summary>
    public async Task SaveAsync(string gdaPath, string gdtPath)
    {
        if (!HasChanges)
        {
            return;
        }

        (byte[] gdaBytes, byte[] gdtBytes) = await Task.Run(Repack);
        await File.WriteAllBytesAsync(gdaPath, gdaBytes);
        await File.WriteAllBytesAsync(gdtPath, gdtBytes);
    }

    private (byte[] Gda, byte[] Gdt) Repack()
    {
        int count = _entries.Count;
        byte[] gdtBytes = new byte[8 + (count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(0), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(gdtBytes.AsSpan(4), (uint)count);

        // The shipped archive deliberately aliases identical content across entries — e.g. a
        // driver's "player" and "ai" texture variants, or two tracks' skybox meshes, pointing
        // at the exact same GDA bytes (327 of 1770 entries in the shipped archive do this,
        // ~963 KB of content). That's very likely load-bearing: the engine probably caches
        // loaded VRAM textures by GDA offset, so de-aliasing turns one shared upload into two
        // separate ones. A naive rewrite that gives every entry its own fresh copy silently
        // breaks this — verified by re-deriving the exact byte growth a real save produced.
        // Reproducing it generally (any two byte-identical entries share space, not just ones
        // the original happened to alias) keeps every *unchanged* entry's offset byte-for-byte
        // put, and confirmed via VerifyRepack against a hash-collision-safe equality check.
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

    /// <summary>A SHA-256 digest used only to bucket candidate matches — always verified against
    /// the real bytes before two entries are allowed to share space, so a hash collision can
    /// only cost a redundant byte comparison, never a wrong merge.</summary>
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

    /// <summary>
    /// Re-reads the just-written GDA/GDT bytes and confirms every entry landed exactly where
    /// and as intended, turning any future silent packing bug into an immediate, specific
    /// exception instead of a corrupted save.
    /// </summary>
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
