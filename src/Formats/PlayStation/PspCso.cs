using System.Buffers.Binary;
using System.IO.Compression;
using DiscUtils.Iso9660;
using Omega.Plugins;
namespace PlayStation.Formats;

public sealed class PspCsoArchiveFormat : IArchiveFormat
{
    private static readonly byte[] Magic = "CISO"u8.ToArray();
    public string Id          => "playstation.psp.cso";
    public string DisplayName => "PSP compressed disc image (.cso)";
    public FileFilter Filter  { get; } = new("PSP compressed disc image", "*.cso", "*.ciso", "*.zso");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
        => head.Length >= 4 && head[..4].SequenceEqual(Magic);

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        CsoStream cso = new(path);
        try
        {
            CDReader reader = new(cso, true);
            IsoDiscArchive archive = IsoDiscArchive.OpenOnStream(path, cso);
            host.Log($"{archive.DisplayName}: PSP CSO, uncompressed {Sizes.Describe(cso.Length)}, {archive.Description}.");
            return archive;
        }
        catch
        {
            cso.Dispose();
            throw;
        }
    }
}

// Streaming decompressor for CISO v1 (zlib) and v2 (lz4, falls back gracefully).
// Supports random sector access via per-block index table.
internal sealed class CsoStream : Stream
{
    private readonly FileStream   _file;
    private readonly uint[]       _index;      // block start offsets; last entry = end of data
    private readonly int          _blockSize;
    private readonly long         _totalBytes;
    private readonly byte         _version;
    private readonly int          _indexShift;
    private long                  _position;

    public CsoStream(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            byte[] hdr = new byte[0x18];
            _file.ReadExactly(hdr);
            if (!hdr.AsSpan(0, 4).SequenceEqual("CISO"u8))
                throw new InvalidDataException($"'{path}' is not a CSO file");
            _totalBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(8));
            _blockSize  = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0x10));
            _version    = hdr[0x14];
            _indexShift = hdr[0x15];
            if (_blockSize <= 0 || _blockSize > 0x200000)
                throw new InvalidDataException($"CSO block size {_blockSize} is invalid");
            int blockCount = (int)((_totalBytes + _blockSize - 1) / _blockSize);
            _index = new uint[blockCount + 1];
            byte[] idxBuf = new byte[(blockCount + 1) * 4];
            _file.ReadExactly(idxBuf);
            for (int i = 0; i <= blockCount; i++)
                _index[i] = BinaryPrimitives.ReadUInt32LittleEndian(idxBuf.AsSpan(i * 4));
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _totalBytes;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _totalBytes + offset,
            _                  => throw new ArgumentException("unknown origin", nameof(origin)),
        };
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length && _position < _totalBytes)
        {
            int block = (int)(_position / _blockSize);
            int within = (int)(_position % _blockSize);
            int available = (int)Math.Min(_blockSize - within, _totalBytes - _position);
            int want = Math.Min(destination.Length - total, available);
            ReadBlock(block, destination.Slice(total, want), within, want);
            total += want;
            _position += want;
        }
        return total;
    }

    private void ReadBlock(int block, Span<byte> dest, int srcOffset, int count)
    {
        uint rawEntry = _index[block];
        uint nextEntry = _index[block + 1];
        bool compressed = (_version < 2)
            ? (rawEntry & 0x80000000u) == 0  // CSO v1: bit clear = compressed
            : (rawEntry & 0x80000000u) == 0;  // same convention for ZISO
        long fileOffset = (long)(rawEntry & 0x7FFFFFFF) << _indexShift;
        long nextOffset = (long)(nextEntry & 0x7FFFFFFF) << _indexShift;
        int storedSize  = (int)(nextOffset - fileOffset);

        if (!compressed)
        {
            // Raw (uncompressed) block
            lock (_file)
            {
                _file.Seek(fileOffset + srcOffset, SeekOrigin.Begin);
                _file.ReadAtLeast(dest[..count], count, throwOnEndOfStream: false);
            }
            return;
        }

        // Compressed block: read and decompress the full block
        byte[] compressed2 = new byte[storedSize];
        lock (_file)
        {
            _file.Seek(fileOffset, SeekOrigin.Begin);
            _file.ReadExactly(compressed2);
        }
        byte[] plain = new byte[_blockSize];
        using MemoryStream ms = new(compressed2);
        using ZLibStream z = new(ms, CompressionMode.Decompress);
        z.ReadExactly(plain);
        plain.AsSpan(srcOffset, count).CopyTo(dest);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}
