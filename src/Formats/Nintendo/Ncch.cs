using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Nintendo.Formats;
using Omega.Disc.IO;
namespace Nintendo.Formats;
internal sealed record ExeFsFile(string Name, uint Offset, uint Size, long DataStart)
{
    public long FileOffset => DataStart + Offset;
    public long FileLength => Size;
}
internal sealed record ExeFs(long DataStart, List<ExeFsFile> Files);
internal sealed record RomFsFile(string Name, uint Offset, uint DataOffset, ulong Length, uint Parent, uint Sibling)
{
    public const uint None = 0xFFFFFFFF;
}
internal sealed record RomFsDirectory(
    string Name, uint Offset, uint Parent, uint FirstFile, uint FirstChild, uint Sibling);
internal sealed class RomFs
{
    private readonly Dictionary<uint, RomFsDirectory> _directories;
    private readonly Dictionary<uint, RomFsFile> _files;
    internal RomFs(long level3, long dataStart, List<RomFsDirectory> directories, List<RomFsFile> files)
    {
        Level3 = level3;
        DataStart = dataStart;
        _directories = directories.ToDictionary(directory => directory.Offset);
        _files = files.ToDictionary(file => (uint)file.Offset);
        DirectoryCount = directories.Count;
        FileCount = files.Count;
    }
    public long Level3 { get; }
    public long DataStart { get; }
    public int DirectoryCount { get; }
    public int FileCount { get; }
    public RomFsDirectory Root => _directories[0];
    public RomFsDirectory? Directory(uint offset) =>
        _directories.TryGetValue(offset, out RomFsDirectory? directory) ? directory : null;
    public RomFsFile? File(uint offset) =>
        _files.TryGetValue(offset, out RomFsFile? file) ? file : null;
    public List<RomFsFile> Files(uint first)
    {
        List<RomFsFile> found = [];
        HashSet<uint> seen = [];
        uint at = first;
        while (at != RomFsFile.None && seen.Add(at))
        {
            if (File(at) is not { } file)
            {
                break;
            }
            found.Add(file);
            at = file.Sibling;
        }
        return found;
    }
    public List<RomFsDirectory> Directories(uint first)
    {
        List<RomFsDirectory> found = [];
        HashSet<uint> seen = [];
        uint at = first;
        while (at != RomFsFile.None && seen.Add(at))
        {
            if (Directory(at) is not { } directory)
            {
                break;
            }
            found.Add(directory);
            at = directory.Sibling;
        }
        return found;
    }
}
internal sealed record NcchHeader
{
    public required string ProductCode { get; init; }
    public required string MakerCode { get; init; }
    public long ExeFsOffset { get; init; }
    public long ExeFsLength { get; init; }
    public long RomFsOffset { get; init; }
    public long RomFsLength { get; init; }
}
internal sealed record Ncch(NcchHeader Header, long Offset, ExeFs? ExeFs, RomFs? RomFs, string? Note)
{
    public const int ExeFsHeaderSize = 0x200;
    public const int ExeFsEntrySize = 0x10;
    public const int ExeFsEntries = 10;
    public const int Level3HeaderSize = 0x28;
    public const string Magic = "NCCH";
    public const string SuperBlockMagic = "IVFC";
    public static readonly string[] ExeFsNames = [".code", "banner", "icon", "logo", "rom"];
    public const int MediaUnit = 0x200;
}
internal static class NcchReader
{
    private const int ScanChunk = 1 << 20;
    private const int MagicOffset = 0x100;
    private const int ProductCodeOffset = 0x50;
    private const int MakerCodeOffset = 0x10;
    private const int ExeFsOffsetField = 0x1A0;
    private const int RomFsOffsetField = 0x1B0;
    public static Ncch? Read(Nintendo3DSArchive archive, long partitionOffset, long partitionLength)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (partitionOffset < 0 || partitionLength < 0x400)
        {
            return null;
        }
        byte[] head = new byte[0x200];
        using Stream window = archive.Range(partitionOffset + MagicOffset, head.Length);
        if (window.Read(head) < head.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> magic = Encoding.ASCII.GetBytes(Ncch.Magic);
        if (!head.AsSpan(0, magic.Length).SequenceEqual(magic))
        {
            return null;
        }
        long ncch = partitionOffset + MagicOffset;
        NcchHeader header = new()
        {
            ProductCode = ReadText(head, ProductCodeOffset, 9),
            MakerCode = ReadText(head, MakerCodeOffset, 2),
            ExeFsOffset = ncch + (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(ExeFsOffsetField, 4))
                * Ncch.MediaUnit),
            ExeFsLength = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(ExeFsOffsetField + 4, 4))
                * Ncch.MediaUnit,
            RomFsOffset = ncch + (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(RomFsOffsetField, 4))
                * Ncch.MediaUnit),
            RomFsLength = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(RomFsOffsetField + 4, 4))
                * Ncch.MediaUnit,
        };
        ExeFs? exeFs = TryReadExeFs(archive, header.ExeFsOffset, archive.ImageLength);
        RomFs? romFs = TryReadRomFs(archive, header.RomFsOffset, archive.ImageLength);
        string? note = null;
        if (exeFs is null || romFs is null)
        {
            (exeFs, romFs) = Find(
                archive, partitionOffset, Math.Min(partitionLength, archive.ImageLength - partitionOffset));
            if (exeFs is null && romFs is null)
            {
                note = "the ExeFS and the RomFS are not readable here, which usually means they are keyed";
            }
            else if (exeFs is null)
            {
                note = "the ExeFS is not readable here, which usually means it is keyed";
            }
            else if (romFs is null)
            {
                note = "the RomFS is not readable here, which usually means it is keyed";
            }
        }
        return new Ncch(header, ncch, exeFs, romFs, note);
    }
    private static (ExeFs? ExeFs, RomFs? RomFs) Find(
        Nintendo3DSArchive archive, long partitionOffset, long partitionLength)
    {
        ExeFs? exeFs = null;
        RomFs? romFs = null;
        byte[] chunk = new byte[ScanChunk];
        for (long at = partitionOffset; at < partitionOffset + partitionLength; at += ScanChunk)
        {
            int read = 0;
            using (Stream window = archive.Range(at, (int)Math.Min(ScanChunk, partitionOffset + partitionLength - at)))
            {
                read = window.ReadAtLeast(chunk, (int)Math.Min(ScanChunk, partitionOffset + partitionLength - at));
            }
            for (int off = 0; off + Ncch.ExeFsHeaderSize <= read; off += Ncch.MediaUnit)
            {
                long imageOffset = at + off;
                exeFs ??= TryParseExeFs(chunk, off, imageOffset, archive.ImageLength);
                if (exeFs is not null && romFs is not null)
                {
                    return (exeFs, romFs);
                }
            }
            for (int off = 0; off + 0x60 <= read; off += Ncch.MediaUnit)
            {
                if (romFs is null && HasMagic(chunk, off, Ncch.SuperBlockMagic))
                {
                    romFs = TryReadRomFs(archive, at + off, archive.ImageLength);
                    if (exeFs is not null)
                    {
                        return (exeFs, romFs);
                    }
                }
            }
        }
        return (exeFs, romFs);
    }
    private static ExeFs? TryReadExeFs(Nintendo3DSArchive archive, long offset, long limit)
    {
        if (offset < 0 || offset + Ncch.ExeFsHeaderSize > limit)
        {
            return null;
        }
        byte[] head = new byte[Ncch.ExeFsHeaderSize];
        using Stream window = archive.Range(offset, head.Length);
        if (window.Read(head) < head.Length)
        {
            return null;
        }
        return TryParseExeFs(head, 0, offset, limit);
    }
    private static ExeFs? TryParseExeFs(byte[] head, int start, long imageOffset, long limit)
    {
        List<ExeFsFile> files = [];
        uint seen = 0;
        for (int i = 0; i < Ncch.ExeFsEntries; i++)
        {
            string name = ReadText(head, start + (i * Ncch.ExeFsEntrySize), 8);
            uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                head.AsSpan(start + (i * Ncch.ExeFsEntrySize) + 8, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                head.AsSpan(start + (i * Ncch.ExeFsEntrySize) + 12, 4));
            if (name.Length == 0 && size == 0)
            {
                continue;
            }
            if (!Ncch.ExeFsNames.Contains(name, StringComparer.Ordinal)
                || fileOffset % 0x10 != 0
                || imageOffset + Ncch.ExeFsHeaderSize + fileOffset + size > limit)
            {
                return null;
            }
            files.Add(new ExeFsFile(name, fileOffset, size, imageOffset + Ncch.ExeFsHeaderSize));
            seen += size;
        }
        if (files.Count == 0 || seen == 0)
        {
            return null;
        }
        return new ExeFs(imageOffset + Ncch.ExeFsHeaderSize, files);
    }
    private static RomFs? TryReadRomFs(Nintendo3DSArchive archive, long offset, long limit)
    {
        if (offset < 0 || offset + 0x60 > limit)
        {
            return null;
        }
        byte[] super = new byte[0x60];
        using Stream window = archive.Range(offset, super.Length);
        if (window.Read(super) < super.Length)
        {
            return null;
        }
        if (!HasMagic(super, 0, Ncch.SuperBlockMagic))
        {
            return null;
        }
        long level3 = offset + 0x1000;
        if (!ReadLevel3(archive, level3, limit, out RomFs? romFs))
        {
            foreach (long candidate in Grow(offset))
            {
                if (ReadLevel3(archive, candidate, limit, out romFs))
                {
                    break;
                }
            }
        }
        return romFs;
    }
    private static bool ReadLevel3(Nintendo3DSArchive archive, long level3, long limit, out RomFs? romFs)
    {
        romFs = null;
        if (level3 < 0 || level3 + Ncch.Level3HeaderSize > limit)
        {
            return false;
        }
        byte[] header = new byte[Ncch.Level3HeaderSize];
        using Stream window = archive.Range(level3, header.Length);
        if (window.Read(header) < header.Length)
        {
            return false;
        }
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint dirMetaOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x0C, 4));
        uint dirMetaLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10, 4));
        uint fileMetaOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1C, 4));
        uint fileMetaLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x20, 4));
        uint fileDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24, 4));
        if (headerLength != Ncch.Level3HeaderSize
            || dirMetaOffset < headerLength
            || dirMetaLength == 0
            || fileMetaOffset < dirMetaOffset + dirMetaLength
            || fileMetaLength == 0
            || dirMetaLength > Array.MaxLength
            || fileMetaLength > Array.MaxLength
            || fileDataOffset < fileMetaOffset + fileMetaLength
            || level3 + dirMetaOffset + dirMetaLength > limit
            || level3 + fileMetaOffset + fileMetaLength > limit)
        {
            return false;
        }
        List<RomFsDirectory> directories = ReadDirTable(archive, level3 + dirMetaOffset, dirMetaLength, limit);
        List<RomFsFile> files = ReadFileTable(archive, level3 + fileMetaOffset, fileMetaLength, limit);
        if (directories.Count == 0 || directories[0].Offset != 0)
        {
            return false;
        }
        romFs = new RomFs(level3, level3 + fileDataOffset, directories, files);
        return true;
    }
    private static List<RomFsDirectory> ReadDirTable(Nintendo3DSArchive archive, long at, uint length, long limit)
    {
        List<RomFsDirectory> found = [];
        byte[] buffer = ReadBuffer(archive, at, length, limit);
        SpanReader reader = SpanReader.Over(buffer);
        while (reader.Offset + 0x18 <= buffer.Length)
        {
            int start = reader.Offset;
            uint parent = reader.UInt32Little();
            uint sibling = reader.UInt32Little();
            uint firstChild = reader.UInt32Little();
            uint firstFile = reader.UInt32Little();
            reader.Skip(4);
            uint nameLength = reader.UInt32Little();
            if (nameLength > 0x400)
            {
                break;
            }
            string name = ReadName(buffer, reader.Offset, (int)nameLength);
            reader.Skip((int)nameLength);
            reader.Align(4);
            found.Add(new RomFsDirectory(name, (uint)start, parent, firstFile, firstChild, sibling));
        }
        return found;
    }
    private static List<RomFsFile> ReadFileTable(Nintendo3DSArchive archive, long at, uint length, long limit)
    {
        List<RomFsFile> found = [];
        byte[] buffer = ReadBuffer(archive, at, length, limit);
        SpanReader reader = SpanReader.Over(buffer);
        while (reader.Offset + 0x20 <= buffer.Length)
        {
            int start = reader.Offset;
            uint parent = reader.UInt32Little();
            uint sibling = reader.UInt32Little();
            uint dataOffset = (uint)reader.UInt64Little();
            ulong size = reader.UInt64Little();
            reader.Skip(4);
            uint nameLength = reader.UInt32Little();
            if (nameLength > 0x400)
            {
                break;
            }
            string name = ReadName(buffer, reader.Offset, (int)nameLength);
            reader.Skip((int)nameLength);
            reader.Align(4);
            found.Add(new RomFsFile(name, (uint)start, dataOffset, size, parent, sibling));
        }
        return found;
    }
    private static byte[] ReadBuffer(Nintendo3DSArchive archive, long at, long length, long limit)
    {
        if (length < 0 || at < 0 || at + length > limit)
        {
            throw new InvalidDataException(
                $"a table at 0x{at:X} of {length.ToString(CultureInfo.InvariantCulture)} byte(s) runs past "
                + $"the end of the {limit.ToString(CultureInfo.InvariantCulture)}-byte image");
        }
        byte[] buffer = new byte[length];
        using Stream window = archive.Range(at, length);
        window.ReadExactly(buffer);
        return buffer;
    }
    private static string ReadName(byte[] buffer, int at, int length)
    {
        ReadOnlySpan<byte> name = buffer.AsSpan(at, length);
        bool littleEndian = true;
        for (int i = 0; i < name.Length; i += 2)
        {
            littleEndian &= name[i + 1] == 0;
        }
        return littleEndian || name.Length < 2
            ? System.Text.Encoding.Unicode.GetString(name)
            : System.Text.Encoding.BigEndianUnicode.GetString(name);
    }
    private static IEnumerable<long> Grow(long offset)
    {
        for (long at = offset + 0x1000; at <= offset + 0x400000; at *= 2)
        {
            yield return at;
        }
    }
    private static string ReadText(byte[] source, int offset, int length)
    {
        int end = source.AsSpan(offset, length).IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(
            source, offset, end < 0 || end >= length ? length : end);
    }
    private static bool HasMagic(byte[] source, int offset, string magic)
    {
        return source.AsSpan(offset, magic.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(magic));
    }
}
