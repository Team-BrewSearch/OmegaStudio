using System.Buffers.Binary;
using System.Globalization;
using Omega.Disc.IO;
using Omega.Plugins;
namespace Nintendo.Formats;
public sealed record NcsdHeader
{
    public required uint ImageSizeInMediaUnits { get; init; }
    public required byte[] MediaId { get; init; }
    public required byte[] FsType { get; init; }
    public required byte[] CryptType { get; init; }
    public required (uint Offset, uint Length)[] Table { get; init; }
    public IEnumerable<(int Slot, long Offset, long Length)> Partitions
    {
        get
        {
            for (int i = 0; i < Table.Length; i++)
            {
                (uint offset, uint length) = Table[i];
                if (offset != 0 && length != 0)
                {
                    yield return (i, (long)offset * N3DS.MediaUnit, (long)length * N3DS.MediaUnit);
                }
            }
        }
    }
}
internal static class N3DS
{
    public const int MediaUnit = 0x200;
    public const int MagicOffset = 0x100;
    public const int TableOffset = 0x120;
    public const int TableEntries = 8;
    public const int TableRecord = 8;
    public const int FsTypeOffset = 0x110;
    public const int CryptTypeOffset = 0x118;
    public const int NcchMagicOffset = 0x100;
    public static NcsdHeader? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        byte[] head = new byte[0x400];
        stream.ReadExactly(head);
        return IsNcsd(head) ? Read(head) : null;
    }
    public static bool IsNcsd(ReadOnlySpan<byte> head)
    {
        return head.Length > MagicOffset + 4 && head.Slice(MagicOffset, 4).SequenceEqual("NCSD"u8);
    }
    public static NcsdHeader Read(ReadOnlySpan<byte> head)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(head.Length, 0x400, nameof(head));
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(MagicOffset + 4, 4));
        byte[] mediaId = head.Slice(0x108, 8).ToArray();
        byte[] fsType = head.Slice(FsTypeOffset, TableEntries).ToArray();
        byte[] cryptType = head.Slice(CryptTypeOffset, TableEntries).ToArray();
        (uint, uint)[] table = new (uint, uint)[TableEntries];
        for (int i = 0; i < TableEntries; i++)
        {
            table[i] = (BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(TableOffset + (i * TableRecord), 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(TableOffset + (i * TableRecord) + 4, 4)));
        }
        return new NcsdHeader
        {
            ImageSizeInMediaUnits = imageSize,
            MediaId = mediaId,
            FsType = fsType,
            CryptType = cryptType,
            Table = table,
        };
    }
    public static string SlotLabel(int slot, long offset, long length)
    {
        string kind = slot switch
        {
            0 => "Game",
            1 => "Manual",
            2 => "Download play",
            3 => "Partition 4",
            _ => $"Partition {slot}",
        };
        return $"{kind} partition, 0x{offset:X8} ({Describe(length)})";
    }
    private static string Describe(long length)
    {
        string[] units = ["bytes", "KiB", "MiB", "GiB"];
        double value = length;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
