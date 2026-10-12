using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
namespace Omega.Disc.Streams
{
    internal static class DiscCrypto
    {
        private const int Block = 16;
        public static Aes ForKey(byte[] key)
        {
            Aes aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            return aes;
        }
        public static void DecryptCbc(
            Aes aes, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> iv, Span<byte> plain, string what = "data")
        {
            ArgumentNullException.ThrowIfNull(aes);
            RequireBlocks(cipher.Length, plain.Length, what);
            aes.DecryptCbc(cipher, iv, plain, PaddingMode.None);
        }
        public static void EncryptCbc(
            Aes aes, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> iv, Span<byte> cipher, string what = "data")
        {
            ArgumentNullException.ThrowIfNull(aes);
            RequireBlocks(plain.Length, cipher.Length, what);
            aes.EncryptCbc(plain, iv, cipher, PaddingMode.None);
        }
        private static void RequireBlocks(int input, int output, string what)
        {
            if (input != output || input % Block != 0)
            {
                throw new InvalidDataException(
                    $"{what} is {input.ToString(CultureInfo.InvariantCulture)} byte(s) long, and CBC works whole "
                    + $"{Block}-byte blocks, so it cannot fill a {output.ToString(CultureInfo.InvariantCulture)}-byte result");
            }
        }
    }
    public sealed class AesCtrStream : Stream
    {
        private readonly Stream _parent;
        private readonly byte[] _counter;
        private readonly byte[] _initial;
        private readonly byte[] _keystream = new byte[16];
        private readonly ICryptoTransform _encryptor;
        private int _keystreamFilled;
        private long _position;
        public AesCtrStream(Stream parent, long offset, long length, byte[] key, byte[] counter)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(counter);
            if (key.Length != 16)
            {
                throw new ArgumentException("an AES-128 key is exactly 16 bytes", nameof(key));
            }
            if (counter.Length != 16)
            {
                throw new ArgumentException("a counter block is exactly 16 bytes", nameof(counter));
            }
            if (offset < 0 || length < 0 || (parent.CanSeek && offset + length > parent.Length))
            {
                throw new InvalidDataException(
                    $"an encrypted region at 0x{offset:X} of {length.ToString(CultureInfo.InvariantCulture)} byte(s) "
                    + $"runs past the end of the {(parent.CanSeek ? parent.Length.ToString(CultureInfo.InvariantCulture) : "unknown")}-byte stream");
            }
            _parent = parent;
            Offset = offset;
            Length = length;
            _counter = (byte[])counter.Clone();
            _initial = (byte[])counter.Clone();
            using Aes aes = Aes.Create();
            aes.Key = (byte[])key.Clone();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            _encryptor = aes.CreateEncryptor();
            _parent.Position = offset;
        }
        public long Offset { get; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
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
            _parent.Position = Offset + _position;
            _parent.ReadExactly(buffer[..count]);
            int done = 0;
            while (done < count)
            {
                if (_keystreamFilled == 0)
                {
                    _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                    Advance(_counter, 1);
                }
                int take = Math.Min(count - done, 16 - _keystreamFilled);
                for (int i = 0; i < take; i++)
                {
                    buffer[done + i] ^= _keystream[_keystreamFilled + i];
                }
                _keystreamFilled = (_keystreamFilled + take) % 16;
                done += take;
            }
            _position += count;
            return count;
        }
        private static void Advance(Span<byte> counter, long blocks)
        {
            long carry = blocks;
            for (int i = counter.Length - 1; i >= 0 && carry > 0; i--)
            {
                long sum = counter[i] + (carry & 0xFF);
                counter[i] = (byte)sum;
                carry = (carry >> 8) + (sum >> 8);
            }
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
            _initial.AsSpan().CopyTo(_counter);
            Advance(_counter, target / 16);
            _keystreamFilled = (int)(target % 16);
            if (_keystreamFilled != 0)
            {
                _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                Advance(_counter, 1);
            }
            _position = target;
            _parent.Position = Offset + target;
            return _position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a decryption stream is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("a decryption stream is read-only");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _encryptor.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    public sealed class Ps3CipherStream : Stream
    {
        public const int Sector = 2048;
        protected override void Dispose(bool disposing)
        {
            if (disposing && _aes is not null)
            {
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }
        private readonly Stream _parent;
        private readonly long _offset;
        private readonly long _length;
        private readonly byte[] _key;
        private readonly Aes _aes;
        private readonly long[] _regions;
        private long _position;
        public Ps3CipherStream(Stream parent, long offset, long length, byte[]? key, params long[] regions)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (key is not null && key.Length != 16)
            {
                throw new ArgumentException("an AES-128 key is exactly 16 bytes", nameof(key));
            }
            if (offset < 0 || length < 0 || (parent.CanSeek && offset + length > parent.Length))
            {
                throw new InvalidDataException(
                    $"a cipher region at 0x{offset:X} of {length.ToString(CultureInfo.InvariantCulture)} byte(s) "
                    + $"runs past the end of the {(parent.CanSeek ? parent.Length.ToString(CultureInfo.InvariantCulture) : "unknown")}-byte stream");
            }
            if (regions.Length % 2 != 0)
            {
                throw new ArgumentException("plain regions are pairs of first sector and count", nameof(regions));
            }
            _parent = parent;
            _offset = offset;
            _length = length;
            _key = key is null ? [] : (byte[])key.Clone();
            _regions = (long[])regions.Clone();
            _parent.Position = offset;
            _aes = IsEncrypted ? DiscCrypto.ForKey(_key) : null!;
        }
        public bool IsEncrypted => _key.Length == 16;
        public static byte[] SectorIv(long sector)
        {
            byte[] iv = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(12, 4), (uint)sector);
            return iv;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
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
            long left = _length - _position;
            if (left <= 0)
            {
                return 0;
            }
            int count = (int)Math.Min(left, buffer.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(Sector);
            try
            {
                int done = 0;
                while (done < count)
                {
                    long sectorStart = _position + done - ((_position + done) % Sector);
                    long sector = sectorStart / Sector;
                    int skip = (int)((_position + done) - sectorStart);
                    int take = Math.Min(count - done, Sector - skip);
                    _parent.Position = _offset + sectorStart;
                    int aligned = (int)Math.Min(Sector, _length - sectorStart);
                    _parent.ReadExactly(rented.AsSpan(0, aligned));
                    if (IsEncrypted && !IsPlain(sector))
                    {
                        byte[] plain = ArrayPool<byte>.Shared.Rent(Sector);
                        try
                        {
                            DiscCrypto.DecryptCbc(
                                _aes, rented.AsSpan(0, aligned), SectorIv(sector), plain, $"a {Sector}-byte sector");
                            plain.AsSpan(skip, take).CopyTo(buffer.Slice(done, take));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(plain);
                        }
                    }
                    else
                    {
                        rented.AsSpan(skip, take).CopyTo(buffer.Slice(done, take));
                    }
                    done += take;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
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
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            _position = target;
            _parent.Position = _offset + target;
            return _position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a cipher stream is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("a cipher stream is read-only");
        }
        public void EncryptSector(ReadOnlySpan<byte> plain, long sector, Span<byte> encrypted)
        {
            if (!IsEncrypted)
            {
                plain.CopyTo(encrypted);
                return;
            }
            DiscCrypto.EncryptCbc(_aes, plain, SectorIv(sector), encrypted, $"a {Sector}-byte sector");
        }
        private bool IsPlain(long sector)
        {
            for (int i = 0; i + 1 < _regions.Length; i += 2)
            {
                if (sector >= _regions[i] && sector < _regions[i] + _regions[i + 1])
                {
                    return true;
                }
            }
            return false;
        }
    }
}
