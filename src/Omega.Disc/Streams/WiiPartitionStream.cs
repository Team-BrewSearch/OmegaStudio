using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Omega.Disc.Streams;
namespace Omega.Disc.Formats.Nintendo
{
    public sealed class WiiPartitionStream : Stream
    {
        private const int SubgroupSize = 8;
        private const int GroupSize = 64;
        private const int HashSpan = 0x400;
        private const int HashLength = 20;
        private const int H0Count = WiiDisc.BlockDataSize / HashSpan;
        private const int H0Size = H0Count * HashLength;
        private const int H0Offset = 0x000;
        private const int H1Offset = 0x280;
        private const int H2Offset = 0x340;
        private const int TableLength = SubgroupSize * HashLength;
        private const int IvOffset = 0x3D0;
        public const int H3TableSize = 0x18000;
        private static readonly byte[] s_zeroIv = new byte[16];
        private readonly Stream _image;
        private readonly SharedStream? _gate;
        private readonly long _offset;
        private readonly long _length;
        private readonly long _physicalLength;
        private readonly Lock _sync = new();
        private readonly byte[] _titleKey;
        private readonly long _h3Table;
        private readonly byte[] _block = new byte[WiiDisc.BlockDataSize];
        private readonly byte[] _raw = new byte[WiiDisc.BlockTotalSize];
        private long _position;
        private long _blockAt = -1;
        public WiiPartitionStream(
            Stream image, long offset, long length, byte[] titleKey, long h3TableOffset = -1, SharedStream? gate = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(titleKey);
            if (titleKey.Length != 16)
            {
                throw new ArgumentException("a title key is exactly 16 bytes", nameof(titleKey));
            }
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            long blocks = (length + WiiDisc.BlockDataSize - 1) / WiiDisc.BlockDataSize;
            long physical = checked(blocks * WiiDisc.BlockTotalSize);
            if (offset < 0 || (image.CanSeek && physical > image.Length - offset))
            {
                throw new InvalidDataException(
                    $"a data area at 0x{offset:X} of {physical.ToString(CultureInfo.InvariantCulture)} stored byte(s) runs "
                    + $"past the end of the {(image.CanSeek ? image.Length.ToString(CultureInfo.InvariantCulture) : "unknown")}-byte image");
            }
            if (h3TableOffset >= 0 && image.CanSeek && h3TableOffset > image.Length - H3TableSize)
            {
                throw new InvalidDataException(
                    $"the H3 table at 0x{h3TableOffset:X} runs past the end of the {image.Length.ToString(CultureInfo.InvariantCulture)}-byte image");
            }
            _image = image;
            _gate = gate;
            _offset = offset;
            _length = length;
            _physicalLength = physical;
            _titleKey = titleKey;
            _h3Table = h3TableOffset;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public bool ImageCanWrite => _image.CanWrite;
        public int ReadAt(long position, Span<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            lock (_sync)
            {
                int total = 0;
                while (total < buffer.Length && position + total < _length)
                {
                    int count = (int)Math.Min(_length - (position + total), buffer.Length - total);
                    total += CopyFrom(position + total, buffer.Slice(total, count));
                }
                return total;
            }
        }
        public void ReadExactlyAt(long position, Span<byte> buffer)
        {
            if (ReadAt(position, buffer) != buffer.Length)
            {
                throw new EndOfStreamException(
                    $"{buffer.Length.ToString(CultureInfo.InvariantCulture)} byte(s) at 0x{position:X} run past the "
                    + $"{_length.ToString(CultureInfo.InvariantCulture)}-byte partition data");
            }
        }
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
            lock (_sync)
            {
                if (_position >= _length || buffer.IsEmpty)
                {
                    return 0;
                }
                int count = (int)Math.Min(_length - _position, buffer.Length);
                int take = CopyFrom(_position, buffer[..count]);
                _position += take;
                return take;
            }
        }
        private int CopyFrom(long position, Span<byte> buffer)
        {
            long block = position / WiiDisc.BlockDataSize;
            int within = (int)(position % WiiDisc.BlockDataSize);
            int take = Math.Min(buffer.Length, WiiDisc.BlockDataSize - within);
            EnsureBlock(block);
            _block.AsSpan(within, take).CopyTo(buffer[..take]);
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            _position = target;
            return _position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a partition stream is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("a partition stream is read-only");
        }
        public void Patch(long decryptedOffset, ReadOnlySpan<byte> payload)
        {
            if (!_image.CanWrite)
            {
                throw new InvalidOperationException(
                    "the image behind this partition is open for reading only, so nothing can be written back");
            }
            if (decryptedOffset < 0 || decryptedOffset > _length - payload.Length)
            {
                throw new InvalidDataException(
                    $"a patch at 0x{decryptedOffset:X} of {payload.Length.ToString(CultureInfo.InvariantCulture)} "
                    + $"byte(s) runs past the end of the {_length}-byte data area");
            }
            if (payload.Length == 0)
            {
                return;
            }
            lock (_sync)
            {
                PatchLocked(decryptedOffset, payload);
            }
        }
        private void PatchLocked(long decryptedOffset, ReadOnlySpan<byte> payload)
        {
            using Aes aes = DiscCrypto.ForKey(_titleKey);
            bool encrypted = !_titleKey.AsSpan().SequenceEqual(new byte[16]);
            long first = decryptedOffset / WiiDisc.BlockDataSize;
            long last = (decryptedOffset + payload.Length - 1) / WiiDisc.BlockDataSize;
            H3Entry(last - (last % GroupSize));
            for (long group = first - (first % GroupSize); group <= last; group += GroupSize)
            {
                long groupStart = group * WiiDisc.BlockDataSize;
                long groupEnd = groupStart + (WiiDisc.BlockDataSize * GroupSize);
                long from = Math.Max(decryptedOffset, groupStart);
                long to = Math.Min(decryptedOffset + payload.Length, groupEnd);
                if (to > from)
                {
                    PatchGroup(
                        aes,
                        encrypted,
                        group,
                        from,
                        payload.Slice((int)(from - decryptedOffset), (int)(to - from)));
                }
            }
            _blockAt = -1;
        }
        private void PatchGroup(Aes aes, bool encrypted, long group, long offset, ReadOnlySpan<byte> payload)
        {
            byte[][] hashes = new byte[GroupSize][];
            byte[][] data = new byte[GroupSize][];
            bool[] present = new bool[GroupSize];
            for (int i = 0; i < GroupSize; i++)
            {
                long at = _offset + ((group + i) * WiiDisc.BlockTotalSize);
                if (((group + i) * WiiDisc.BlockDataSize) >= _length)
                {
                    continue;
                }
                byte[] raw = new byte[WiiDisc.BlockTotalSize];
                ReadExactly(at, raw);
                hashes[i] = DecryptHeader(aes, encrypted, raw);
                data[i] = DecryptData(aes, encrypted, raw);
                present[i] = true;
            }
            long at2 = offset;
            int done = 0;
            while (done < payload.Length)
            {
                long cluster = at2 / WiiDisc.BlockDataSize;
                int i = (int)(cluster - group);
                if (!present[i])
                {
                    throw new InvalidOperationException(
                        $"cluster {cluster.ToString(CultureInfo.InvariantCulture)} of the partition is not stored in "
                        + "the image, so there is nothing to patch there");
                }
                int within = (int)(at2 % WiiDisc.BlockDataSize);
                int take = (int)Math.Min(payload.Length - done, WiiDisc.BlockDataSize - within);
                payload.Slice(done, take).CopyTo(data[i].AsSpan(within, take));
                done += take;
                at2 += take;
            }
            for (int i = 0; i < GroupSize; i++)
            {
                if (!present[i])
                {
                    continue;
                }
                for (int h = 0; h < H0Count; h++)
                {
                    byte[] digest = Hash(data[i].AsSpan(h * HashSpan, HashSpan));
                    digest.CopyTo(hashes[i].AsSpan(H0Offset + (h * HashLength), HashLength));
                }
            }
            byte[][] subgroup = new byte[SubgroupSize][];
            for (int s = 0; s < SubgroupSize; s++)
            {
                byte[] table = SeedTable(hashes, present, H1Offset);
                for (int j = 0; j < SubgroupSize; j++)
                {
                    int i = (s * SubgroupSize) + j;
                    if (present[i])
                    {
                        byte[] digest = Hash(hashes[i].AsSpan(H0Offset, H0Size));
                        digest.CopyTo(table.AsSpan(j * HashLength, HashLength));
                    }
                }
                subgroup[s] = table;
            }
            byte[] groupTable = SeedTable(hashes, present, H2Offset);
            for (int s = 0; s < SubgroupSize; s++)
            {
                byte[] digest = Hash(subgroup[s]);
                digest.CopyTo(groupTable.AsSpan(s * HashLength, HashLength));
            }
            for (int i = 0; i < GroupSize; i++)
            {
                if (!present[i])
                {
                    continue;
                }
                subgroup[i / SubgroupSize].CopyTo(hashes[i].AsSpan(H1Offset, TableLength));
                groupTable.CopyTo(hashes[i].AsSpan(H2Offset, TableLength));
            }
            for (int i = 0; i < GroupSize; i++)
            {
                if (!present[i])
                {
                    continue;
                }
                byte[] header = EncryptHeader(aes, encrypted, hashes[i]);
                byte[] iv = header.AsSpan(IvOffset, 16).ToArray();
                byte[] body = EncryptData(aes, encrypted, data[i], iv);
                WriteAt(_offset + ((group + i) * WiiDisc.BlockTotalSize), header);
                WriteAt(_offset + ((group + i) * WiiDisc.BlockTotalSize) + WiiDisc.BlockHeaderSize, body);
            }
            RefreshH3(group, groupTable);
        }
        private static byte[] SeedTable(byte[][] hashes, bool[] present, int offset)
        {
            byte[] table = new byte[TableLength];
            for (int i = 0; i < GroupSize; i++)
            {
                if (present[i])
                {
                    hashes[i].AsSpan(offset, TableLength).CopyTo(table);
                    return table;
                }
            }
            return table;
        }
        private long H3Entry(long firstBlock)
        {
            if (_h3Table < 0)
            {
                throw new InvalidDataException(
                    "this partition names no H3 table, so its hash chain cannot be rebuilt after a write");
            }
            long ordinal = firstBlock / GroupSize;
            long at = checked(_h3Table + (ordinal * HashLength));
            if (at + HashLength > _h3Table + H3TableSize)
            {
                throw new InvalidDataException(
                    $"group {ordinal.ToString(CultureInfo.InvariantCulture)} falls past the end of the "
                    + $"{H3TableSize.ToString(CultureInfo.InvariantCulture)}-byte H3 table");
            }
            return at;
        }
        private void RefreshH3(long group, ReadOnlySpan<byte> h2Table)
        {
            WriteAt(H3Entry(group), Hash(h2Table));
        }
        private static byte[] DecryptHeader(Aes aes, bool encrypted, ReadOnlySpan<byte> raw)
        {
            byte[] plain = raw[..WiiDisc.BlockHeaderSize].ToArray();
            if (encrypted)
            {
                byte[] clear = new byte[WiiDisc.BlockHeaderSize];
                DiscCrypto.DecryptCbc(aes, plain, s_zeroIv, clear, "a hash header");
                return clear;
            }
            return plain;
        }
        private static byte[] EncryptHeader(Aes aes, bool encrypted, ReadOnlySpan<byte> plain)
        {
            if (!encrypted)
            {
                return plain.ToArray();
            }
            byte[] cipher = new byte[WiiDisc.BlockHeaderSize];
            DiscCrypto.EncryptCbc(aes, plain, s_zeroIv, cipher, "a hash header");
            return cipher;
        }
        private static byte[] DecryptData(Aes aes, bool encrypted, ReadOnlySpan<byte> raw)
        {
            byte[] plain = raw[WiiDisc.BlockHeaderSize..].ToArray();
            if (encrypted)
            {
                byte[] clear = new byte[WiiDisc.BlockDataSize];
                DiscCrypto.DecryptCbc(aes, plain, raw.Slice(IvOffset, 16), clear, "a data block");
                return clear;
            }
            return plain;
        }
        private static byte[] EncryptData(Aes aes, bool encrypted, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> iv)
        {
            if (!encrypted)
            {
                return plain.ToArray();
            }
            byte[] cipher = new byte[WiiDisc.BlockDataSize];
            DiscCrypto.EncryptCbc(aes, plain, iv, cipher, "a data block");
            return cipher;
        }
        [SuppressMessage(
            "Security",
            "CA5350:Do Not Use Weak Cryptographic Algorithms",
            Justification = "The Wii disc hash chain is specified as SHA-1; CORR-WII requires recomputing it exactly.")]
        private static byte[] Hash(ReadOnlySpan<byte> bytes) => SHA1.HashData(bytes);
        private void ReadExactly(long at, byte[] buffer)
        {
            if (_gate is { } gate)
            {
                gate.ReadExactlyAt(at, buffer);
                return;
            }
            _image.Position = at;
            _image.ReadExactly(buffer);
        }
        private void WriteAt(long at, ReadOnlySpan<byte> bytes)
        {
            if (_gate is { } gate)
            {
                gate.WriteAt(at, bytes);
                return;
            }
            _image.Position = at;
            _image.Write(bytes);
        }
        private void EnsureBlock(long block)
        {
            if (_blockAt == block)
            {
                return;
            }
            long relative = block * WiiDisc.BlockTotalSize;
            if (relative > _physicalLength - WiiDisc.BlockTotalSize)
            {
                throw new InvalidDataException(
                    $"block {block.ToString(CultureInfo.InvariantCulture)} lies past the partition's stored data");
            }
            _blockAt = -1;
            ReadExactly(_offset + relative, _raw);
            const int data = WiiDisc.BlockDataSize;
            if (_titleKey.AsSpan().SequenceEqual(new byte[16]))
            {
                _raw.AsSpan(WiiDisc.BlockHeaderSize, data).CopyTo(_block);
            }
            else
            {
                using Aes aes = DiscCrypto.ForKey(_titleKey);
                DiscCrypto.DecryptCbc(
                    aes,
                    _raw.AsSpan(WiiDisc.BlockHeaderSize, data),
                    _raw.AsSpan(IvOffset, 16),
                    _block,
                    "a data block");
            }
            _blockAt = block;
        }
    }
}
