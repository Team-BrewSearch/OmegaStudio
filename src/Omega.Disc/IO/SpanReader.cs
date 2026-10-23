using System.Buffers.Binary;
using System.Globalization;
using System.Text;
namespace Omega.Disc.IO
{
    public ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;
        private SpanReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }
        public readonly int Remaining => _bytes.Length - _offset;
        public int Offset
        {
            readonly get => _offset;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "a reader cannot sit before the first byte");
                }
                _offset = value;
            }
        }
        public static SpanReader Over(ReadOnlySpan<byte> bytes)
        {
            return new SpanReader(bytes);
        }
        public static SpanReader At(ReadOnlySpan<byte> bytes, int offset)
        {
            SpanReader reader = new(bytes) { Offset = offset };
            return reader;
        }
        public readonly byte Peek(int offset)
        {
            Require(offset, 1);
            return _bytes[offset];
        }
        public byte Byte()
        {
            Require(_offset, 1);
            byte value = _bytes[_offset];
            _offset += 1;
            return value;
        }
        public ushort UInt16Little()
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(PeekSpan(2));
            _offset += 2;
            return value;
        }
        public ushort UInt16Big()
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(PeekSpan(2));
            _offset += 2;
            return value;
        }
        public uint UInt32Little()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(PeekSpan(4));
            _offset += 4;
            return value;
        }
        public uint UInt32Big()
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(PeekSpan(4));
            _offset += 4;
            return value;
        }
        public ulong UInt64Little()
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(PeekSpan(8));
            _offset += 8;
            return value;
        }
        public ulong UInt64Big()
        {
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(PeekSpan(8));
            _offset += 8;
            return value;
        }
        public string Ascii(int count)
        {
            return Text(count, Encoding.ASCII);
        }
        public string Latin1(int count)
        {
            return Text(count, Encoding.Latin1);
        }
        public string Text(int count, Encoding encoding)
        {
            ArgumentNullException.ThrowIfNull(encoding);
            byte[] raw = Take(count);
            int end = raw.AsSpan().IndexOf((byte)0);
            return encoding.GetString(raw, 0, end < 0 ? raw.Length : end);
        }
        public string RawText(int count, Encoding encoding)
        {
            ArgumentNullException.ThrowIfNull(encoding);
            return encoding.GetString(Take(count));
        }
        public byte[] Take(int count)
        {
            Require(_offset, count);
            byte[] value = new byte[count];
            Fill(value);
            return value;
        }
        public void Fill(Span<byte> destination)
        {
            PeekSpan(destination.Length).CopyTo(destination);
            _offset += destination.Length;
        }
        public ReadOnlySpan<byte> PeekSpan(int count)
        {
            Require(_offset, count);
            return _bytes.Slice(_offset, count);
        }
        public readonly ReadOnlySpan<byte> PeekSpan(int offset, int count)
        {
            Require(offset, count);
            return _bytes.Slice(offset, count);
        }
        public void Skip(int count)
        {
            Require(_offset, count);
            _offset += count;
        }
        public void Seek(int offset)
        {
            Offset = offset;
        }
        public void Align(int unit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unit);
            int rem = _offset % unit;
            if (rem != 0)
            {
                Skip(unit - rem);
            }
        }
        private readonly void Require(int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > _bytes.Length)
            {
                throw new InvalidDataException(
                    $"reading {count.ToString(CultureInfo.InvariantCulture)} byte(s) at "
                    + $"0x{Math.Max(0, offset).ToString("X", CultureInfo.InvariantCulture)} runs past the end of a "
                    + $"{_bytes.Length.ToString(CultureInfo.InvariantCulture)}-byte header");
            }
        }
        public static int Padding(int offset, int unit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unit);
            int rem = offset % unit;
            return rem == 0 ? 0 : unit - rem;
        }
    }
}
