using System.Globalization;
using Omega.Disc.Vfs;
namespace Omega.Disc.Streams
{
    public sealed class WindowStream : Stream
    {
        private readonly Stream _parent;
        private readonly long _offset;
        private readonly long _length;
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
            _offset = offset;
            _length = length;
            Position = 0;
            _parent.Position = _offset;
        }
        public long WindowOffset => _offset;
        public long WindowLength => _length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get; set; }
        public override void Flush()
        {
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
        public override int Read(Span<byte> buffer)
        {
            long left = _length - Position;
            if (left <= 0)
            {
                return 0;
            }
            int count = (int)Math.Min(left, buffer.Length);
            _parent.Position = _offset + Position;
            int read = _parent.Read(buffer[..count]);
            Position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException("a window stream is read-only");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("a window stream is read-only");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
