using System.Globalization;
using Omega.Disc.IO;
using Omega.Disc.Streams;
using Omega.Disc.Vfs;
using Omega.Plugins;
namespace Omega.Disc.Formats.Nintendo
{
    public sealed record WbfsSlot(int Slot, byte[] Header, ushort[] LbaTable)
    {
        private int? _usedSectors;
        private long? _length;
        public int UsedSectors => _usedSectors ??= CountUsed(LbaTable);
        public long StoredSectors => _length ??= ComputeLength(LbaTable);
        private static int CountUsed(ushort[] table)
        {
            int used = 0;
            foreach (ushort sector in table)
            {
                if (sector != 0)
                {
                    used++;
                }
            }
            return used;
        }
        private static long ComputeLength(ushort[] table)
        {
            long used = 1;
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i] != 0)
                {
                    used = i + 1;
                }
            }
            return used;
        }
    }
    public sealed record WbfsGeometry(int HdSectorShift, int WbfsSectorShift, uint SectorCount)
    {
        public int HdSectorSize => 1 << HdSectorShift;
        public int WbfsSectorSize => 1 << WbfsSectorShift;
        public long TotalWiiSectors => (SectorCount * (long)HdSectorSize) / WbfsContainer.WiiSector;
        public long TotalWbfsSectors => TotalWiiSectors >> (WbfsSectorShift - WbfsContainer.WiiSectorShiftValue);
        public int SectorsPerDisc
        {
            get
            {
                int shift = WbfsSectorShift - WbfsContainer.WiiSectorShiftValue;
                return (int)(WbfsContainer.WiiSectorsPerDisc >> shift);
            }
        }
        public int FreeBitmapSector => (WbfsSectorSize - (int)(TotalWbfsSectors / 8)) >> HdSectorShift;
        public int DiscInfoSize
        {
            get
            {
                int raw = WbfsContainer.DiscHeaderSize + (SectorsPerDisc * 2);
                int rem = raw % HdSectorSize;
                return rem == 0 ? raw : raw + (HdSectorSize - rem);
            }
        }
        public int MaxSlots
        {
            get
            {
                int perSlot = DiscInfoSize / HdSectorSize;
                return Math.Min((FreeBitmapSector - 1) / perSlot, HdSectorSize - WbfsContainer.DiscTableOffset);
            }
        }
    }
    public sealed class WbfsContainer
    {
        public const int WiiSector = 0x8000;
        public const uint WiiSectorsPerDisc = 143432 * 2;
        public const int DiscHeaderSize = 0x100;
        public const int DiscTableOffset = 0x0C;
        public const int WiiSectorShiftValue = 15;
        public const int MaxClusterShift = 30;
        private const uint Magic = 0x57424653;
        private static int DeferredBudget => (int)Math.Min(MemoryBudget.DeferredWrites, int.MaxValue / 2);
        private readonly SharedStream _shared;
        private readonly WbfsSlot?[] _slots;
        private readonly bool[] _free;
        private readonly Dictionary<(int Slot, int Cluster), byte[]> _deferred = [];
        private readonly Lock _sync = new();
        private long _deferredBytes;
        private WbfsContainer(SharedStream shared, WbfsGeometry geometry, WbfsSlot?[] slots, bool[] free)
        {
            _shared = shared;
            Geometry = geometry;
            _slots = slots;
            _free = free;
        }
        public WbfsGeometry Geometry { get; }
        public SharedStream Shared => _shared;
        public IEnumerable<WbfsSlot> Slots => _slots.Where(slot => slot is not null).Select(slot => slot!);
        public int DiscCount => _slots.Count(slot => slot is not null);
        public int FreeSectors => _free.Count(value => value);
        public long DeferredBytes
        {
            get
            {
                lock (_sync)
                {
                    return _deferredBytes;
                }
            }
        }
        public int DeferredClusters
        {
            get
            {
                lock (_sync)
                {
                    return _deferred.Count;
                }
            }
        }
        public static string JournalPath(string container) => container + ".omega-journal";
        public bool IsFree(int sector)
        {
            int index = sector - 1;
            return index < 0 || index >= _free.Length || _free[index];
        }
        public long FirstDataSector
        {
            get
            {
                long bitmapBytes = (Geometry.TotalWbfsSectors + 7) / 8;
                long bitmapEnd = ((long)Geometry.FreeBitmapSector * Geometry.HdSectorSize) + bitmapBytes;
                long rem = bitmapEnd % Geometry.HdSectorSize;
                long end = rem == 0 ? bitmapEnd : bitmapEnd + (Geometry.HdSectorSize - rem);
                return end / Geometry.WbfsSectorSize;
            }
        }
        public static WbfsContainer Open(System.IO.Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek)
            {
                throw new ArgumentException("a WBFS container is read from a seekable stream", nameof(stream));
            }
            return OpenCore(new Omega.Disc.Streams.SharedStream(stream));
        }
        public static WbfsContainer Open(Omega.Disc.Streams.SharedStream shared)
        {
            ArgumentNullException.ThrowIfNull(shared);
            return OpenCore(shared);
        }
        private static WbfsContainer OpenCore(Omega.Disc.Streams.SharedStream shared)
        {
            System.IO.Stream stream = shared.Stream;
            if (shared.Stream.Position != 0)
            {
                shared.Stream.Position = 0;
            }
            byte[] head = new byte[512];
            stream.ReadExactly(head);
            SpanReader reader = SpanReader.Over(head);
            uint magic = reader.UInt32Big();
            uint sectors = reader.UInt32Big();
            int hdShift = reader.Byte();
            int wbfsShift = reader.Byte();
            WbfsGeometry geometry = new(hdShift, wbfsShift, sectors);
            if (magic != Magic)
            {
                throw new InvalidDataException("a WBFS container starts with the letters WBFS, in big-endian");
            }
            if (geometry.HdSectorShift is < 9 or > 20)
            {
                throw new InvalidDataException(
                    $"a WBFS header at 0x08 declares container sectors of 2^{geometry.HdSectorShift} bytes, "
                    + "and the format uses 2^9 to 2^20");
            }
            if (geometry.WbfsSectorShift <= geometry.HdSectorShift)
            {
                throw new InvalidDataException(
                    $"a WBFS header at 0x08 and 0x09 declares clusters of 2^{geometry.WbfsSectorShift} bytes inside "
                    + $"container sectors of 2^{geometry.HdSectorShift} bytes, and a cluster has to be the larger");
            }
            if (geometry.WbfsSectorShift is < WiiSectorShiftValue or > MaxClusterShift
                || geometry.WbfsSectorSize > shared.Length)
            {
                throw new InvalidDataException(
                    $"a WBFS header at 0x09 declares clusters of 2^{geometry.WbfsSectorShift} bytes, and the format "
                    + $"uses 2^{WiiSectorShiftValue} to 2^{MaxClusterShift}, no larger than the "
                    + $"{shared.Length}-byte file");
            }
            long declared = geometry.HdSectorSize * (long)geometry.SectorCount;
            if (declared > shared.Length * 1.01)
            {
                throw new InvalidDataException(
                    $"a WBFS header claims {geometry.SectorCount} container sectors of {geometry.HdSectorSize} bytes, "
                    + $"which is {declared} bytes, past the end of the {stream.Length}-byte file");
            }
            if (geometry.MaxSlots < 1)
            {
                throw new InvalidDataException(
                    $"a WBFS header declares {geometry.TotalWbfsSectors} clusters of {geometry.WbfsSectorSize} bytes, "
                    + "whose free-block map leaves no room for a disc table");
            }
            RecoverJournal(shared);
            return new WbfsContainer(shared, geometry, ReadSlots(shared.Stream, geometry), ReadFreeBlocks(shared.Stream, geometry));
        }
        public Stream OpenDisc(WbfsSlot slot)
        {
            ArgumentNullException.ThrowIfNull(slot);
            return new WbfsWindowStream(this, slot);
        }
        public long DataOffset(WbfsSlot slot, int index)
        {
            ArgumentNullException.ThrowIfNull(slot);
            if (index < 0 || index >= slot.LbaTable.Length)
            {
                throw new InvalidDataException(
                    $"cluster {index} is outside the {slot.LbaTable.Length}-cluster table of slot {slot.Slot}");
            }
            ushort stored = slot.LbaTable[index];
            if (stored == 0)
            {
                return -1;
            }
            if (stored - 1 >= Geometry.TotalWbfsSectors)
            {
                throw new InvalidDataException(
                    $"slot {slot.Slot} names WBFS sector {stored} for cluster {index}, and the container only has "
                    + $"{Geometry.TotalWbfsSectors} of them");
            }
            return (long)stored * Geometry.WbfsSectorSize;
        }
        public long WriteOffset(WbfsSlot slot, int index)
        {
            long at = DataOffset(slot, index);
            if (at < 0)
            {
                throw new InvalidDataException(
                    $"cluster {index} of slot {slot.Slot} was never stored, so there is nothing to patch there");
            }
            if (at < FirstDataSector * Geometry.WbfsSectorSize)
            {
                throw new InvalidDataException(
                    $"slot {slot.Slot} names WBFS sector {slot.LbaTable[index]} for cluster {index}, which lands in "
                    + $"the container's own metadata rather than in its data area, at 0x{at:X}");
            }
            long reach = at + Geometry.WbfsSectorSize;
            if (reach > _shared.Length)
            {
                throw new InvalidDataException(
                    $"slot {slot.Slot} names WBFS sector {slot.LbaTable[index]} for cluster {index}, which runs to "
                    + $"0x{reach:X} past the end of the {_shared.Length}-byte container");
            }
            if (IsFree(slot.LbaTable[index]))
            {
                throw new InvalidDataException(
                    $"slot {slot.Slot} names WBFS sector {slot.LbaTable[index]} for cluster {index}, and the "
                    + "container's own free-block map says that sector is unallocated");
            }
            return at;
        }
        public void DeferWrite(WbfsSlot slot, int index, ReadOnlySpan<byte> bytes)
        {
            ArgumentNullException.ThrowIfNull(slot);
            if (bytes.Length != Geometry.WbfsSectorSize)
            {
                throw new InvalidDataException(
                    $"a deferred write to cluster {index} of slot {slot.Slot} is {bytes.Length} byte(s), and a "
                    + $"cluster is {Geometry.WbfsSectorSize}");
            }
            lock (_sync)
            {
                long previous = _deferred.TryGetValue((slot.Slot, index), out byte[]? old) ? old.Length : 0;
                long total = _deferredBytes - previous + bytes.Length;
                if (total > DeferredBudget)
                {
                    throw new InvalidDataException(
                        $"{total} bytes of writes are waiting for the container, past "
                        + $"the {DeferredBudget} a single save may hold. Save, or make the edit in smaller pieces.");
                }
                _deferred[(slot.Slot, index)] = bytes.ToArray();
                _deferredBytes = total;
            }
        }
        public byte[]? Deferred(WbfsSlot slot, int index)
        {
            lock (_sync)
            {
                return _deferred.TryGetValue((slot.Slot, index), out byte[]? bytes) ? bytes : null;
            }
        }
        public void DiscardWrites()
        {
            lock (_sync)
            {
                _deferred.Clear();
                _deferredBytes = 0;
            }
        }
        public void CommitWrites()
        {
            lock (_sync)
            {
                CommitLocked();
            }
        }
        private void CommitLocked()
        {
            if (_deferred.Count == 0)
            {
                return;
            }
            List<(long At, byte[] Bytes)> writes = [];
            foreach (((int slotIndex, int clusterIndex), byte[] bytes) in _deferred)
            {
                WbfsSlot? slot = _slots[slotIndex];
                if (slot is null)
                {
                    throw new InvalidDataException(
                        $"a deferred write names slot {slotIndex}, which holds no disc");
                }
                writes.Add((WriteOffset(slot, clusterIndex), bytes));
            }
            writes.Sort((a, b) => a.At.CompareTo(b.At));
            string? journal = _shared.Path is { } path ? JournalPath(path) : null;
            bool reopened = !_shared.CanWrite;
            _shared.Reopen(readOnly: false);
            try
            {
                List<(long At, byte[] Bytes)> before = WriteJournal(journal, writes);
                int applied = 0;
                try
                {
                    foreach ((long destination, byte[] cluster) in writes)
                    {
                        applied++;
                        _shared.WriteAt(destination, cluster);
                    }
                    _shared.Flush();
                    byte[] verify = new byte[Geometry.WbfsSectorSize];
                    foreach ((long check, byte[] expected) in writes)
                    {
                        _shared.ReadExactlyAt(check, verify.AsSpan(0, expected.Length));
                        if (!verify.AsSpan(0, expected.Length).SequenceEqual(expected))
                        {
                            throw new IOException(
                                $"a cluster written to the container at 0x{check:X} did not read back as it was written");
                        }
                    }
                }
                catch (Exception failure)
                {
                    Restore(before, applied, journal, failure);
                    throw;
                }
                if (journal is not null)
                {
                    File.Delete(journal);
                }
                _deferred.Clear();
                _deferredBytes = 0;
            }
            finally
            {
                if (reopened)
                {
                    _shared.Reopen(readOnly: true);
                }
            }
        }
        private static ReadOnlySpan<byte> JournalMagic => "OMJ1"u8;
        private static ReadOnlySpan<byte> JournalDone => "DONE"u8;
        private List<(long At, byte[] Bytes)> WriteJournal(string? journal, List<(long At, byte[] Bytes)> writes)
        {
            List<(long At, byte[] Bytes)> before = new(writes.Count);
            foreach ((long at, byte[] bytes) in writes)
            {
                byte[] original = new byte[bytes.Length];
                _shared.ReadExactlyAt(at, original);
                before.Add((at, original));
            }
            if (journal is null)
            {
                return before;
            }
            using (FileStream file = new(journal, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(file))
            {
                writer.Write(JournalMagic);
                foreach ((long at, byte[] original) in before)
                {
                    writer.Write(at);
                    writer.Write(original.Length);
                    writer.Write(original);
                }
                writer.Write(JournalDone);
                writer.Write(before.Count);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            return before;
        }
        private void Restore(List<(long At, byte[] Bytes)> before, int applied, string? journal, Exception failure)
        {
            try
            {
                for (int i = applied - 1; i >= 0; i--)
                {
                    _shared.WriteAt(before[i].At, before[i].Bytes);
                }
                _shared.Flush();
                if (journal is not null)
                {
                    File.Delete(journal);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"saving the container failed ({failure.Message}) and putting the original clusters back also "
                    + $"failed ({ex.Message}). The container may be partly written; "
                    + (journal is null
                        ? "restore it from its backup."
                        : $"the original clusters are kept in {Path.GetFileName(journal)} and are put back the next "
                            + "time the container is opened, or restore it from its backup."),
                    failure);
            }
        }
        private static void RecoverJournal(SharedStream shared)
        {
            if (shared.Path is not { } path || JournalPath(path) is not { } journal || !File.Exists(journal))
            {
                return;
            }
            List<(long At, byte[] Bytes)>? entries = ReadJournal(journal, shared.Length);
            if (entries is null)
            {
                File.Delete(journal);
                return;
            }
            bool reopened = !shared.CanWrite;
            shared.Reopen(readOnly: false);
            try
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    shared.WriteAt(entries[i].At, entries[i].Bytes);
                }
                shared.Flush();
                File.Delete(journal);
            }
            finally
            {
                if (reopened)
                {
                    shared.Reopen(readOnly: true);
                }
            }
        }
        internal static List<(long At, byte[] Bytes)>? ReadJournal(string journal, long containerLength)
        {
            using FileStream file = new(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(file);
            try
            {
                if (!reader.ReadBytes(4).AsSpan().SequenceEqual(JournalMagic))
                {
                    return null;
                }
                List<(long At, byte[] Bytes)> entries = [];
                while (file.Length - file.Position >= 8)
                {
                    long mark = file.Position;
                    if (reader.ReadBytes(4).AsSpan().SequenceEqual(JournalDone))
                    {
                        return reader.ReadInt32() == entries.Count && file.Position == file.Length ? entries : null;
                    }
                    file.Position = mark;
                    long at = reader.ReadInt64();
                    int length = reader.ReadInt32();
                    if (at < 0 || length <= 0 || length > containerLength - at || length > file.Length - file.Position)
                    {
                        return null;
                    }
                    entries.Add((at, reader.ReadBytes(length)));
                }
                return null;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }
        public void VerifyTables()
        {
            byte[] probe = new byte[1];
            if (_deferred.Count == 0)
            {
                return;
            }
            foreach (WbfsSlot slot in Slots)
            {
                for (int i = 0; i < slot.LbaTable.Length; i++)
                {
                    if (slot.LbaTable[i] == 0)
                    {
                        continue;
                    }
                    long at = DataOffset(slot, i);
                    if (at < 0)
                    {
                        continue;
                    }
                    if (_shared.ReadAt(at, probe) <= 0)
                    {
                        throw new InvalidDataException(
                            $"slot {slot.Slot} names WBFS sector {slot.LbaTable[i]} for cluster {i}, and the "
                            + $"container has nothing at 0x{at:X}");
                    }
                }
            }
        }
        private static WbfsSlot?[] ReadSlots(System.IO.Stream stream, WbfsGeometry geometry)
        {
            stream.Position = WbfsContainer.DiscTableOffset;
            byte[] discTable = new byte[geometry.MaxSlots];
            stream.ReadExactly(discTable);
            WbfsSlot?[] slots = new WbfsSlot?[geometry.MaxSlots];
            byte[] table = new byte[geometry.DiscInfoSize];
            int perSlot = geometry.DiscInfoSize / geometry.HdSectorSize;
            for (int slot = 0; slot < geometry.MaxSlots; slot++)
            {
                if (discTable[slot] == 0)
                {
                    continue;
                }
                long at = (1 + (slot * (long)perSlot)) * geometry.HdSectorSize;
                if (at + geometry.DiscInfoSize > stream.Length)
                {
                    continue;
                }
                stream.Position = at;
                stream.ReadExactly(table);
                slots[slot] = ReadSlot(slot, table, geometry);
            }
            return slots;
        }
        private static WbfsSlot ReadSlot(int slot, byte[] table, WbfsGeometry geometry)
        {
            byte[] header = table.AsSpan(0, DiscHeaderSize).ToArray();
            ushort[] lba = new ushort[geometry.SectorsPerDisc];
            SpanReader reader = SpanReader.Over(table);
            reader.Seek(DiscHeaderSize);
            for (int i = 0; i < lba.Length; i++)
            {
                lba[i] = reader.UInt16Big();
            }
            return new WbfsSlot(slot, header, lba);
        }
        private static bool[] ReadFreeBlocks(Stream stream, WbfsGeometry geometry)
        {
            long count = Math.Min(geometry.TotalWbfsSectors, ((long)geometry.FreeBitmapSector * geometry.HdSectorSize) * 8);
            bool[] free = new bool[count];
            if (count == 0)
            {
                return free;
            }
            byte[] bitmap = new byte[(count + 7) / 8];
            stream.Position = (long)geometry.FreeBitmapSector * geometry.HdSectorSize;
            if (stream.Length - stream.Position >= bitmap.Length)
            {
                stream.ReadExactly(bitmap);
            }
            for (long i = 0; i < count; i++)
            {
                free[i] = (bitmap[i / 8] & (1 << (int)(i % 8))) == 0;
            }
            return free;
        }
    }
    public sealed class WbfsWindowStream : Stream
    {
        private const int MaxRead = 16 * 1024 * 1024;
        private readonly WbfsContainer _wbfs;
        private readonly WbfsSlot _slot;
        private readonly long _sectorSize;
        private readonly long _length;
        private long _position;
        internal WbfsWindowStream(WbfsContainer wbfs, WbfsSlot slot)
        {
            _wbfs = wbfs;
            _slot = slot;
            _sectorSize = wbfs.Geometry.WbfsSectorSize;
            _length = slot.StoredSectors * _sectorSize;
        }
        public override long Length => _slot.StoredSectors * _sectorSize;
        public long Capacity => _slot.LbaTable.Length * (long)_sectorSize;
        public override bool CanRead => _wbfs.Shared.CanWrite || true;
        public override bool CanSeek => true;
        public override bool CanWrite => _wbfs.Shared.CanWrite || _wbfs.Shared.CanReopen;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public override void Flush()
        {
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
        public override int Read(Span<byte> buffer)
        {
            long left = Length - _position;
            if (left <= 0)
            {
                return 0;
            }
            int count = (int)Math.Min(left, buffer.Length);
            long at = _position;
            int done = 0;
            while (done < count)
            {
                int index = (int)((at + done) / _sectorSize);
                int within = (int)((at + done) % _sectorSize);
                int take = (int)Math.Min(count - done, _sectorSize - within);
                if (_wbfs.Deferred(_slot, index) is { } pending)
                {
                    pending.AsSpan(within, take).CopyTo(buffer.Slice(done, take));
                }
                else
                {
                    long stored = _wbfs.DataOffset(_slot, index);
                    if (stored < 0)
                    {
                        buffer.Slice(done, take).Clear();
                    }
                    else
                    {
                        ReadCluster(stored + within, buffer.Slice(done, take));
                    }
                }
                done += take;
            }
            _position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            _position = target;
            return _position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a reconstructed disc is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteThrough(buffer);
        }
        private void WriteThrough(ReadOnlySpan<byte> buffer)
        {
            if (!CanWrite)
            {
                throw new NotSupportedException(
                    "the container behind this reconstructed image is open for reading only");
            }
            if (buffer.Length > MaxRead)
            {
                throw new InvalidDataException(
                    $"a write of {buffer.Length} byte(s) is past the {MaxRead} a single edit may be");
            }
            if (_position + buffer.Length > Length)
            {
                throw new InvalidDataException(
                    $"writing {buffer.Length.ToString(CultureInfo.InvariantCulture)} byte(s) at "
                    + $"0x{_position:X} runs past the end of the {Length}-byte reconstructed image");
            }
            long at = _position;
            int done = 0;
            while (done < buffer.Length)
            {
                int index = (int)((at + done) / _sectorSize);
                long stored = _wbfs.DataOffset(_slot, index);
                if (stored < 0)
                {
                    throw new InvalidDataException(
                        $"cluster {index} of slot {_slot.Slot} was never stored, so there is nothing to patch");
                }
                int within = (int)((at + done) % _sectorSize);
                int take = (int)Math.Min(buffer.Length - done, _sectorSize - within);
                byte[] cluster = _wbfs.Deferred(_slot, index)?.ToArray() ?? ReadClusterAt(stored, (int)_sectorSize);
                buffer.Slice(done, take).CopyTo(cluster.AsSpan(within, take));
                _wbfs.DeferWrite(_slot, index, cluster);
                done += take;
            }
            _position += buffer.Length;
        }
        private byte[] ReadClusterAt(long from, int length)
        {
            byte[] cluster = new byte[length];
            ReadCluster(from, cluster);
            return cluster;
        }
        private void ReadCluster(long from, Span<byte> target)
        {
            int into = 0;
            while (into < target.Length)
            {
                int read = _wbfs.Shared.ReadAt(from + into, target[into..]);
                if (read <= 0)
                {
                    target[into..].Clear();
                    return;
                }
                into += read;
            }
        }
    }
}
