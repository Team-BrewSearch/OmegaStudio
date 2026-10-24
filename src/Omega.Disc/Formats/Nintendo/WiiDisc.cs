using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Omega.Disc.IO;
using Omega.Disc.Streams;
using Omega.Disc.Vfs;
namespace Omega.Disc.Formats.Nintendo
{
    public sealed record WiiDiscHeader(
        string GameId,
        string MakerCode,
        byte DiscNumber,
        byte DiscVersion,
        string Title,
        uint WiiMagic,
        byte DisableHashes,
        byte DisableEncryption)
    {
        public bool IsWii => WiiMagic == WiiDisc.WiiMagic;
    }
    public sealed record WiiPartitionEntry(long Offset, uint Type);
    public sealed record WiiPartitionHeader
    {
        public required long Offset { get; init; }
        public uint Type { get; init; }
        public long DataOffset { get; init; }
        public uint DataSize { get; init; }
        public long TmdOffset { get; init; }
        public uint TmdSize { get; init; }
        public long H3TableOffset { get; init; }
        public byte[]? EncryptedTitleKey { get; init; }
        public byte[]? TitleId { get; init; }
        public byte CommonKeyIndex { get; init; }
        public byte[]? TmdTitleId { get; init; }
        public ushort TmdTitleVersion { get; init; }
        public long DataSizeBytes => (long)DataSize << WiiDisc.OffsetShift;
        public long BlockCount => DataSizeBytes / WiiDisc.BlockTotalSize;
        public long DecryptedSize => BlockCount * WiiDisc.BlockDataSize;
    }
    public sealed record WiiFstEntry(string Name, long Offset, uint Length, uint NextIndex, int Index, bool IsDirectory);
    public static class WiiCommonKeys
    {
        public static readonly byte[] Retail =
        [
            0xEB, 0xE4, 0x2A, 0x22, 0x5E, 0x85, 0x93, 0xE4,
            0x48, 0xD9, 0xC5, 0x45, 0x73, 0x81, 0xAA, 0xF7
        ];
        public static readonly byte[] Korean =
        [
            0x63, 0xB8, 0x2B, 0xB4, 0xF4, 0x61, 0x4E, 0x2E,
            0x13, 0xF2, 0xFE, 0xFB, 0xBA, 0x4C, 0x9B, 0x7E
        ];
        public static readonly byte[] Devkit =
        [
            0xA1, 0x60, 0x4A, 0x6A, 0x71, 0x23, 0xB5, 0x29,
            0xAE, 0x8B, 0xEC, 0x32, 0xC8, 0x16, 0xFC, 0xAA
        ];
        public static byte[] ForIndex(byte index)
        {
            return index switch
            {
                0 => Retail,
                1 => Korean,
                2 => Devkit,
                _ => throw new InvalidDataException(
                    $"a ticket names common key {index}, and this build only knows 0 and 1"),
            };
        }
    }
    public sealed class WiiDisc : IDisposable
    {
        public const int BlockHeaderSize = 0x400;
        public const int BlockDataSize = 0x7C00;
        public const int BlockTotalSize = BlockHeaderSize + BlockDataSize;
        public const uint WiiMagic = 0x5D1C9EA3;
        public const int OffsetShift = 2;
        public const long PartitionTableOffset = 0x40000;
        public const int MaxFileSystemSize = 128 * 1024 * 1024;
        private const int PartitionInfoSize = 0x10000;
        private const int TitleMetadataHead = 0x420;
        private readonly Stream _image;
        private readonly SharedStream? _gate;
        private readonly List<WiiPartitionHeader> _partitions = [];
        private WiiDisc(Stream image, WiiDiscHeader header, SharedStream? gate = null)
        {
            _image = image;
            _gate = gate;
            Header = header;
        }
        public WiiDiscHeader Header { get; }
        public IReadOnlyList<WiiPartitionHeader> Partitions => _partitions;
        public WiiPartitionHeader? GamePartition => _partitions.FirstOrDefault(part => part.Type == 0);
        public static WiiDisc Open(System.IO.Stream image)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (!image.CanSeek)
            {
                throw new ArgumentException("a Wii disc is read from a seekable stream", nameof(image));
            }
            image.Position = 0;
            WiiDiscHeader read = ReadHeader(image)
                ?? throw new InvalidDataException(
                    "the word at 0x18 is not a Wii disc's, and this reader only takes Wii images");
            WiiDisc disc = new(image, read);
            disc.ReadPartitionTable();
            return disc;
        }
        public static WiiDisc Open(SharedStream shared)
        {
            ArgumentNullException.ThrowIfNull(shared);
            WiiDiscHeader read = ReadHeader(shared.Stream)
                ?? throw new InvalidDataException(
                    "the word at 0x18 is not a Wii disc's, and this reader only takes Wii images");
            WiiDisc disc = new(shared.Stream, read, shared);
            disc.ReadPartitionTable();
            return disc;
        }
        private static WiiDiscHeader? ReadHeader(System.IO.Stream image)
        {
            long at = image.Position;
            image.Position = 0;
            byte[] head = new byte[0x400];
            image.ReadExactly(head);
            image.Position = at;
            WiiDiscHeader header = ReadHeader(head);
            return header.IsWii ? header : null;
        }
        public static byte[] TitleKey(WiiPartitionHeader partition)
        {
            ArgumentNullException.ThrowIfNull(partition);
            if (partition.EncryptedTitleKey is null || partition.TitleId is null)
            {
                throw new InvalidDataException(
                    $"the ticket of the partition at 0x{partition.Offset:X} holds no key");
            }
            byte[] iv = new byte[16];
            partition.TitleId.AsSpan(0, 8).CopyTo(iv);
            using Aes aes = DiscCrypto.ForKey(WiiCommonKeys.ForIndex(partition.CommonKeyIndex));
            byte[] plain = new byte[16];
            DiscCrypto.DecryptCbc(aes, partition.EncryptedTitleKey, iv, plain, "a title key");
            return plain;
        }
        public Stream OpenPartition(WiiPartitionHeader partition)
        {
            ArgumentNullException.ThrowIfNull(partition);
            if (partition.DataSizeBytes < BlockTotalSize)
            {
                throw new InvalidDataException(
                    $"the partition at 0x{partition.Offset:X} declares a {partition.DataSizeBytes}-byte data area, "
                    + $"and one block alone is {BlockTotalSize}");
            }
            byte[] titleKey = Header.DisableEncryption != 0 ? new byte[16] : TitleKey(partition);
            return new WiiPartitionStream(
                _image,
                partition.Offset + partition.DataOffset,
                partition.DecryptedSize,
                titleKey,
                partition.H3TableOffset == 0 ? -1 : checked(partition.Offset + partition.H3TableOffset),
                _gate);
        }
        public IReadOnlyList<WiiFstEntry> ReadFileSystem(WiiPartitionHeader partition)
        {
            ArgumentNullException.ThrowIfNull(partition);
            using Stream data = OpenPartition(partition);
            byte[] boot = new byte[0x430];
            data.ReadExactly(boot);
            SpanReader reader = SpanReader.At(boot, 0x424);
            long fstOffset = (long)reader.UInt32Big() << OffsetShift;
            long fstSize = (long)reader.UInt32Big() << OffsetShift;
            if (fstSize > MaxFileSystemSize)
            {
                throw new InvalidDataException(
                    $"the partition at 0x{partition.Offset:X} declares a {fstSize}-byte file system table, past "
                    + $"the {MaxFileSystemSize} bytes a Wii could load");
            }
            if (fstSize < 24 || fstOffset + fstSize > partition.DecryptedSize)
            {
                throw new InvalidDataException(
                    $"the partition at 0x{partition.Offset:X} puts a {fstSize}-byte file system table at "
                    + $"0x{fstOffset:X}, outside its {partition.DecryptedSize}-byte data area");
            }
            data.Position = fstOffset;
            byte[] table = new byte[fstSize];
            data.ReadExactly(table);
            return ReadFileSystemTable(table);
        }
        public static IReadOnlyList<WiiFstEntry> ReadFileSystemTable(byte[] table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (table.Length < 24)
            {
                throw new InvalidDataException(
                    $"a file system table of {table.Length} bytes cannot hold the root entry");
            }
            return ReadEntries(table);
        }
        private static WiiFstEntry[] ReadEntries(byte[] table)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(8, 4));
            if (count < 1 || (count * 12L) + 1 > table.Length)
            {
                throw new InvalidDataException(
                    $"a file system table of {table.Length} bytes cannot hold {count} entries");
            }
            uint nameTable = count * 12;
            WiiFstEntry[] entries = new WiiFstEntry[count];
            for (int index = 0; index < count; index++)
            {
                SpanReader reader = SpanReader.At(table, index * 12);
                uint nameField = reader.UInt32Big();
                uint offset = reader.UInt32Big();
                uint size = reader.UInt32Big();
                bool isDirectory = (nameField & 0xFF000000) != 0;
                uint nameOffset = nameField & 0x00FFFFFF;
                string name = index == 0
                    ? string.Empty
                    : ReadName(table, nameTable, nameOffset, index);
                entries[index] = new WiiFstEntry(
                    name,
                    isDirectory ? 0 : (long)offset << OffsetShift,
                    isDirectory ? 0 : size,
                    isDirectory ? size : (uint)(index + 1),
                    index,
                    isDirectory);
            }
            return entries;
        }
        private static string ReadName(byte[] table, uint nameTable, uint nameOffset, int index)
        {
            if (nameOffset + nameTable >= table.Length)
            {
                throw new InvalidDataException(
                    $"entry {index} of a file system table names offset 0x{nameOffset:X}, past the end of its "
                    + $"{table.Length}-byte table");
            }
            int at = (int)(nameTable + nameOffset);
            int end = table.AsSpan(at).IndexOf((byte)0);
            if (end < 0)
            {
                throw new InvalidDataException(
                    $"the name of entry {index} of a file system table never ends");
            }
            return Encoding.ASCII.GetString(table, at, end);
        }
        private static WiiDiscHeader ReadHeader(byte[] head)
        {
            SpanReader reader = SpanReader.Over(head);
            string gameId = reader.Ascii(4);
            string maker = reader.Ascii(2);
            byte disc = reader.Byte();
            byte version = reader.Byte();
            reader.Seek(0x18);
            uint magic = reader.UInt32Big();
            reader.Seek(0x20);
            string title = reader.Text(0x40, Encoding.UTF8);
            byte disableHashes = reader.Peek(0x60);
            byte disableEncryption = reader.Peek(0x61);
            return new WiiDiscHeader(gameId, maker, disc, version, title, magic, disableHashes, disableEncryption);
        }
        private void ReadPartitionTable()
        {
            _image.Position = PartitionTableOffset;
            byte[] area = new byte[0x20];
            _image.ReadExactly(area);
            for (int group = 0; group < 4; group++)
            {
                SpanReader reader = SpanReader.At(area, group * 8);
                uint count = reader.UInt32Big();
                uint tableValue = reader.UInt32Big();
                if (count == 0 || tableValue == 0)
                {
                    continue;
                }
                long tableOffset = (long)tableValue << 2;
                if (count * 8L > PartitionInfoSize)
                {
                    throw new InvalidDataException(
                        $"group {group} of the partition table lists {count} partitions, more than the "
                        + $"{PartitionInfoSize}-byte partition information area holds");
                }
                if (tableOffset + (count * 8L) > _image.Length)
                {
                    throw new InvalidDataException(
                        $"the partition table at 0x{tableOffset:X} holds {count} entries, past the end of the "
                        + $"{_image.Length}-byte image");
                }
                _image.Position = tableOffset;
                byte[] entries = new byte[count * 8];
                _image.ReadExactly(entries);
                for (int i = 0; i < count; i++)
                {
                    SpanReader entry = SpanReader.At(entries, i * 8);
                    long offset = (long)entry.UInt32Big() << 2;
                    uint type = entry.UInt32Big();
                    if (offset == 0 || type == 0xFFFFFFFF)
                    {
                        return;
                    }
                    _partitions.Add(ReadPartitionHeader(offset, type));
                }
            }
        }
        private WiiPartitionHeader ReadPartitionHeader(long offset, uint type)
        {
            _image.Position = offset;
            byte[] head = new byte[0x2C0];
            _image.ReadExactly(head);
            SpanReader reader = SpanReader.Over(head);
            reader.Seek(0x2A4);
            uint tmdSize = reader.UInt32Big();
            uint tmdValue = reader.UInt32Big();
            reader.Seek(0x2B4);
            uint h3Value = reader.UInt32Big();
            reader.Seek(0x2B8);
            uint dataValue = reader.UInt32Big();
            uint dataSize = reader.UInt32Big();
            WiiPartitionHeader partition = new()
            {
                Offset = offset,
                Type = type,
                TmdSize = tmdSize,
                TmdOffset = (long)tmdValue << 2,
                H3TableOffset = (long)h3Value << 2,
                DataOffset = (long)dataValue << 2,
                DataSize = dataSize,
            };
            partition = ReadTicket(partition);
            return ReadTitleMetadata(partition);
        }
        private WiiPartitionHeader ReadTicket(WiiPartitionHeader partition)
        {
            _image.Position = partition.Offset;
            byte[] ticket = new byte[0x2A4];
            _image.ReadExactly(ticket);
            SpanReader reader = SpanReader.Over(ticket);
            uint signatureType = reader.UInt32Big();
            int body = SignatureBodySize(signatureType);
            if (body + 0xB2 > ticket.Length)
            {
                throw new InvalidDataException(
                    $"the ticket of the partition at 0x{partition.Offset:X} is too short for a "
                    + $"{signatureType:X8} signature");
            }
            return partition with
            {
                EncryptedTitleKey = ticket.AsSpan(body + 0x7F, 0x10).ToArray(),
                TitleId = ticket.AsSpan(body + 0x9C, 8).ToArray(),
                CommonKeyIndex = ticket[body + 0xB1],
            };
        }
        private WiiPartitionHeader ReadTitleMetadata(WiiPartitionHeader partition)
        {
            if (partition.TmdOffset == 0 || partition.TmdSize == 0)
            {
                return partition;
            }
            long at = partition.Offset + partition.TmdOffset;
            if (at + partition.TmdSize > _image.Length)
            {
                return partition;
            }
            _image.Position = at;
            byte[] tmd = new byte[Math.Min(partition.TmdSize, TitleMetadataHead)];
            _image.ReadExactly(tmd);
            SpanReader reader = SpanReader.Over(tmd);
            uint signatureType = reader.UInt32Big();
            int body = SignatureBodySize(signatureType);
            if (body + 0x1E0 > tmd.Length)
            {
                return partition;
            }
            return partition with
            {
                TmdTitleId = tmd.AsSpan(body + 0x18C, 8).ToArray(),
                TmdTitleVersion = BinaryPrimitives.ReadUInt16BigEndian(tmd.AsSpan(body + 0x1DC, 2)),
            };
        }
        public void Dispose()
        {
        }
        private static int SignatureBodySize(uint signatureType)
        {
            uint signatureSize = signatureType switch
            {
                0x00010000u => 0x200,
                _ => 0x100,
            };
            return (int)((4 + signatureSize + 0x3F) & ~0x3Fu);
        }
    }
}
