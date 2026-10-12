using System.Buffers.Binary;
using System.Text;
using Omega.Disc.IO;
using Omega.Disc.Streams;
using Omega.Disc.Vfs;
namespace Omega.Disc.Formats.Xbox
{
    public static class XgdBases
    {
        public const long Xiso = 0;
        public const long Xgd1 = 0x18300000;
        public const long Xgd2 = 0xFD90000;
        public const long Xgd3 = 0x2080000;
        public const long Xgd2Trial = 0x89D80000;
        public static readonly long[] Candidates = [Xgd3, Xgd2, Xgd1, Xgd2Trial, Xiso];
        public const int DescriptorSector = 0x20;
        public const int Sector = 0x800;
    }
    public sealed record GdfxEntry(string Name, uint FirstSector, uint Length, bool IsDirectory)
    {
    }
    public sealed class GdfxVolume
    {
        public const int DepthCap = 32;
        public const int VisitCap = 1 << 20;
        public const int RecordHeaderSize = 0x0E;
        public const byte DirectoryFlag = 0x10;
        public const uint Filler = 0xFFFFFFFF;
        private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
        private readonly Stream _image;
        private readonly List<GdfxEntry> _entries = [];
        private readonly Dictionary<uint, List<GdfxEntry>> _tables = [];
        private GdfxVolume(Stream image, long baseOffset)
        {
            _image = image;
            BaseOffset = baseOffset;
        }
        public long BaseOffset { get; }
        public uint RootSector { get; private init; }
        public uint RootSize { get; private init; }
        public IReadOnlyList<GdfxEntry> Entries => _entries;
        public int DirectoryCount => _entries.Count(entry => entry.IsDirectory);
        public IReadOnlyList<GdfxEntry> Table(uint sector, uint size)
        {
            lock (_tables)
            {
                if (!_tables.TryGetValue(sector, out List<GdfxEntry>? entries))
                {
                    entries = ReadTable(sector, size, _image, BaseOffset);
                    _tables[sector] = entries;
                }
                return entries;
            }
        }
        public static GdfxVolume? Open(Stream image)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (!image.CanSeek)
            {
                throw new ArgumentException("an Xbox 360 image is read from a seekable stream", nameof(image));
            }
            foreach (long candidate in XgdBases.Candidates)
            {
                GdfxVolume? volume = ReadAt(image, candidate);
                if (volume is not null)
                {
                    return volume;
                }
            }
            return StrideScan(image);
        }
        public static GdfxVolume? ReadAt(Stream image, long baseOffset)
        {
            ArgumentNullException.ThrowIfNull(image);
            long descriptor = baseOffset + (XgdBases.DescriptorSector * (long)XgdBases.Sector);
            if (baseOffset < 0 || descriptor + 0x20 > image.Length)
            {
                return null;
            }
            image.Position = descriptor;
            byte[] head = new byte[0x20];
            image.ReadExactly(head);
            if (!head.AsSpan(0, s_magic.Length).SequenceEqual(s_magic))
            {
                return null;
            }
            SpanReader reader = SpanReader.Over(head);
            reader.Seek(0x14);
            uint rootSector = reader.UInt32Little();
            uint rootSize = reader.UInt32Little();
            GdfxVolume volume = new(image, baseOffset)
            {
                RootSector = rootSector,
                RootSize = rootSize,
            };
            if (rootSector == 0 || rootSize == 0)
            {
                throw new InvalidDataException(
                    $"the volume descriptor at 0x{descriptor:X} names a root table of {rootSize} byte(s) at sector "
                    + $"{rootSector}");
            }
            volume.Walk();
            return volume;
        }
        public Stream OpenRead(GdfxEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.IsDirectory)
            {
                throw new InvalidDataException($"'{entry.Name}' is a directory");
            }
            return new WindowStream(_image, BaseOffset + (entry.FirstSector * (long)XgdBases.Sector), entry.Length);
        }
        public long DataOffset(GdfxEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return BaseOffset + (entry.FirstSector * (long)XgdBases.Sector);
        }
        private void Walk()
        {
            List<GdfxEntry> found = [];
            HashSet<uint> visited = [];
            Stack<(uint Sector, uint Size, int Depth)> pending = new();
            pending.Push((RootSector, RootSize, 0));
            while (pending.Count > 0)
            {
                (uint sector, uint size, int depth) = pending.Pop();
                if (depth > DepthCap || !visited.Add(sector))
                {
                    continue;
                }
                foreach (GdfxEntry entry in Table(sector, size))
                {
                    if (entry.IsDirectory)
                    {
                        pending.Push((entry.FirstSector, entry.Length, depth + 1));
                    }
                    found.Add(entry);
                    if (found.Count > VisitCap)
                    {
                        throw new InvalidDataException(
                            $"the game partition at 0x{BaseOffset:X} names more than {VisitCap} entries, "
                            + "which no disc does; its tables are corrupt");
                    }
                }
            }
            found.Sort((left, right) =>
                left.IsDirectory != right.IsDirectory
                    ? (left.IsDirectory ? -1 : 1)
                    : string.CompareOrdinal(left.Name, right.Name));
            _entries.AddRange(found);
        }
        private List<GdfxEntry> ReadTable(uint sector, uint size, Stream image, long baseOffset)
        {
            List<GdfxEntry> found = [];
            while (size >= XgdBases.Sector && sector != 0 && sector != 0xFFFFFFFF)
            {
                long at = BaseOffset + (sector * (long)XgdBases.Sector);
                if (at + XgdBases.Sector > _image.Length)
                {
                    throw new InvalidDataException(
                        $"the game partition at 0x{BaseOffset:X} points at table sector {sector}, past the end of "
                        + $"the {_image.Length}-byte image");
                }
                image.Position = at;
                byte[] table = new byte[XgdBases.Sector];
                image.ReadExactly(table);
                if (BinaryPrimitives.ReadUInt32LittleEndian(table) == Filler)
                {
                    sector++;
                    size -= XgdBases.Sector;
                    continue;
                }
                SpanReader reader = SpanReader.Over(table);
                int at2 = 0;
                while (at2 + RecordHeaderSize < XgdBases.Sector)
                {
                    reader.Seek(at2);
                    ushort left = reader.UInt16Little();
                    ushort right = reader.UInt16Little();
                    uint first = reader.UInt32Little();
                    uint length = reader.UInt32Little();
                    byte flags = reader.Byte();
                    byte nameLength = reader.Byte();
                    if (left == 0xFFFF && right == 0xFFFF && first == 0xFFFFFFFF)
                    {
                        break;
                    }
                    if (nameLength == 0)
                    {
                        break;
                    }
                    string name = reader.Latin1(nameLength);
                    at2 += (RecordHeaderSize + nameLength + 3) & ~3;
                    if (name.Length == 0 || name == ".")
                    {
                        continue;
                    }
                    found.Add(new GdfxEntry(name, first, length, (flags & DirectoryFlag) != 0));
                    if (found.Count > VisitCap)
                    {
                        throw new InvalidDataException(
                            $"table sector {sector} of the game partition at 0x{BaseOffset:X} names more than "
                            + $"{VisitCap} entries; its table is corrupt");
                    }
                }
                sector++;
                size -= XgdBases.Sector;
            }
            return found;
        }
        private static GdfxVolume? StrideScan(Stream image)
        {
            const long Step = 0x100000;
            const long Cap = 0x100000000L;
            for (long at = Step; at < Cap && at + 0x10020 < image.Length; at += Step)
            {
                GdfxVolume? volume = ReadAt(image, at);
                if (volume is not null)
                {
                    return volume;
                }
            }
            return null;
        }
    }
}
