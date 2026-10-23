using System.Globalization;
using Omega.Disc.Vfs;
namespace Omega.Disc.Streams
{
    public sealed class WindowStream : Stream
    {
        private readonly Stream _parent;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
        private readonly long _offset;
        private readonly long _length;
        private long _position;
        public WindowStream(Stream parent, long offset, long length)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (offset < 0 || length < 0 || (parent.CanSeek && offset + length > parent.Length))
            {
                throw new InvalidDataException(
                    $"a window at 0x{offset:X} of {length.ToString(CultureInfo.InvariantCulture)} byte(s) runs past "
                    + $"the end of the {(parent.CanSeek ? parent.Length.ToString(CultureInfo.InvariantCulture) : "unknown")}-byte stream");
            }
            _parent = parent;
            _handle = parent is FileStream { CanWrite: false } file ? file.SafeFileHandle : null;
            _offset = offset;
            _length = length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
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
            if (left <= 0 || buffer.IsEmpty)
            {
                return 0;
            }
            Span<byte> into = buffer[..(int)Math.Min(left, buffer.Length)];
            int read;
            if (_handle is not null)
            {
                read = RandomAccess.Read(_handle, into, _offset + _position);
            }
            else
            {
                lock (_parent)
                {
                    _parent.Position = _offset + _position;
                    read = _parent.Read(into);
                }
            }
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a window stream is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("a window stream is read-only");
        }
    }
}
