using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Nintendo.Formats.Nodes;
using Omega.Disc.Formats.Nintendo;
using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Nintendo.Formats;

public sealed class GcnDiscArchiveFormat : IArchiveFormat
{
    private const int MinHead = 0x430;
    public string Id          => "nintendo.gcn";
    public string DisplayName => "GameCube disc image (.iso/.gcm)";
    public FileFilter Filter  { get; } = new("GameCube disc image", "*.iso", "*.gcm");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length < MinHead) return false;
        if (WiiDisc.IsGcn(head)) return true;
        // Some GCN rips have a non-standard magic word; validate structure instead
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".gcm", StringComparison.OrdinalIgnoreCase))
            return false;
        uint wii = BinaryPrimitives.ReadUInt32BigEndian(head[0x18..]);
        if (wii == WiiDisc.WiiMagic) return false;
        for (int i = 0; i < 4; i++) if (head[i] < 0x20 || head[i] > 0x7E) return false;
        uint fstOff  = BinaryPrimitives.ReadUInt32BigEndian(head[0x424..]);
        uint fstSize = BinaryPrimitives.ReadUInt32BigEndian(head[0x428..]);
        return fstOff > 0 && fstSize >= 24;
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        GcnDiscArchive archive = GcnDiscArchive.Open(path);
        host.Log($"{archive.DisplayName}: GameCube disc, {archive.GameId}, \"{archive.Title}\", "
            + $"{archive.FileCount} file(s).");
        return archive;
    }
}

public sealed class GcnCisoFormat : IArchiveFormat
{
    public string Id          => "nintendo.gcn.ciso";
    public string DisplayName => "GameCube compressed disc image (.ciso)";
    public FileFilter Filter  { get; } = new("GameCube compressed disc image", "*.ciso", "*.gcz");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return false;
        if (!head[..4].SequenceEqual("CISO"u8)) return false;
        // GCN CISO uses large blocks (2 MB = 0x200000); PSP CSO stores u64 total at [0x08]
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        return blockSize >= 0x10000;
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        GcnCisoStream ciso = new(path);
        try
        {
            GcnDiscArchive archive = GcnDiscArchive.OpenStream(path, ciso);
            host.Log($"{archive.DisplayName}: GCN CISO, uncompressed {Sizes.Describe(ciso.Length)}, "
                + $"{archive.GameId}, \"{archive.Title}\", {archive.FileCount} file(s).");
            return archive;
        }
        catch
        {
            ciso.Dispose();
            throw;
        }
    }
}

internal sealed class GcnDiscArchive : IArchive
{
    private readonly Stream _stream;
    private GcnDiscArchive(
        string path,
        Stream stream,
        string gameId,
        string title,
        int fileCount,
        IReadOnlyList<WiiFstEntry> fst)
    {
        _stream    = stream;
        GameId     = gameId;
        Title      = title;
        FileCount  = fileCount;
        DisplayName = Path.GetFileName(path);
        Root = new GcnRootNode(this, DisplayName, fst);
    }
    internal Stream Stream  => _stream;
    public string GameId        { get; }
    public string Title         { get; }
    public int    FileCount     { get; }
    public string DisplayName   { get; }
    public IAssetNode Root      { get; }
    public bool   IsDirty       => false;
    public IReadOnlyList<string> BackupPaths => [];
    public void Save()    => throw new InvalidOperationException($"{DisplayName} is read-only.");
    public void Dispose() => _stream.Dispose();

    public static GcnDiscArchive Open(string path)
        => OpenStream(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

    public static GcnDiscArchive OpenStream(string displayPath, Stream stream)
    {
        try
        {
            byte[] head = new byte[0x430];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(head);

            string gameId = Encoding.ASCII.GetString(head, 0, 4);
            string title  = Encoding.UTF8.GetString(head, 0x20, 0xC0).TrimEnd('\0').Trim();

            long fstOffset = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x424));
            long fstSize   = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x428));

            if (fstSize < 24 || fstOffset + fstSize > stream.Length)
                throw new InvalidDataException(
                    $"GCN disc places the FST ({fstSize} bytes) at 0x{fstOffset:X} in a {stream.Length}-byte image.");

            stream.Seek(fstOffset, SeekOrigin.Begin);
            byte[] table = new byte[fstSize];
            stream.ReadExactly(table);

            IReadOnlyList<WiiFstEntry> fst = WiiDisc.ReadFileSystemTable(table, 0);
            int fileCount = fst.Count(e => !e.IsDirectory);
            return new GcnDiscArchive(displayPath, stream, gameId, title, fileCount, fst);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}

internal sealed class GcnRootNode : AssetNode
{
    public GcnRootNode(GcnDiscArchive archive, string displayName, IReadOnlyList<WiiFstEntry> fst)
        : base(displayName, displayName, AssetKinds.Container,
               $"{archive.GameId}, {archive.Title}")
    {
        ChildFactory = () => GcnFstWalker.Walk(fst, 0, archive);
    }
}

internal sealed class GcnFolderNode : AssetNode
{
    public GcnFolderNode(WiiFstEntry entry, IReadOnlyList<WiiFstEntry> fst, GcnDiscArchive archive)
        : base(entry.Name, entry.Name, AssetKinds.Container, "")
    {
        ChildFactory = () => GcnFstWalker.Walk(fst, entry.Index, archive);
    }
}

internal sealed class GcnFileNode : StreamingAssetNode
{
    public GcnFileNode(WiiFstEntry entry, GcnDiscArchive archive)
        : base(
            entry.Name, entry.Name,
            new DiscWindow(archive.Stream, entry.Offset, entry.Length),
            AssetKinds.Auto,
            Sizes.Describe((long)entry.Length))
    { }
    public override string Extension  => Path.GetExtension(Name) is { Length: > 1 } e ? e : ".bin";
    public override string FileStem   => Path.GetFileNameWithoutExtension(Name);
    public override bool   CanReplaceBytes => false;
    protected override void Write(byte[] payload) =>
        throw new InvalidOperationException($"'{Name}' is in a read-only GCN disc image.");

    private sealed class DiscWindow(Stream stream, long offset, uint size) : IPayloadSource
    {
        public long Length => size;
        public int Head(Span<byte> destination)
        {
            int count = (int)Math.Min(destination.Length, size);
            if (count <= 0) return 0;
            lock (stream) { stream.Seek(offset, SeekOrigin.Begin); return stream.ReadAtLeast(destination[..count], count, throwOnEndOfStream: false); }
        }
        public byte[] ReadAll()
        {
            if (size == 0) return [];
            byte[] payload = new byte[size];
            lock (stream) { stream.Seek(offset, SeekOrigin.Begin); stream.ReadExactly(payload); }
            return payload;
        }
    }
}

// GCN CISO: 0x8000-byte fixed header (magic + u32 blockSize + 0x7FF8 bitmap), raw blocks follow.
// bitmap[i]=1 → logical block i is physically stored; bitmap[i]=0 → sparse (zeros).
internal sealed class GcnCisoStream : Stream
{
    private readonly FileStream _file;
    private readonly int        _blockSize;
    private readonly int[]      _physMap;   // logical → physical block index (-1 = sparse)
    private readonly long       _totalBytes;
    private long                _position;

    public GcnCisoStream(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            byte[] hdr = new byte[0x8000];
            _file.ReadExactly(hdr);
            if (!hdr.AsSpan(0, 4).SequenceEqual("CISO"u8))
                throw new InvalidDataException($"'{path}' is not a GCN CISO file");
            _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (_blockSize <= 0 || _blockSize > 0x1000000)
                throw new InvalidDataException($"GCN CISO block size 0x{_blockSize:X} is invalid");

            // Find highest set bit in bitmap to determine logical disc size
            int bitmapLen = hdr.Length - 8;
            int logBlocks = 0;
            for (int i = bitmapLen - 1; i >= 0; i--)
            {
                if (hdr[8 + i] != 0) { logBlocks = i + 1; break; }
            }
            _totalBytes = (long)logBlocks * _blockSize;
            _physMap = new int[logBlocks];
            int phys = 0;
            for (int i = 0; i < logBlocks; i++)
                _physMap[i] = hdr[8 + i] != 0 ? phys++ : -1;
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
            int block  = (int)(_position / _blockSize);
            int within = (int)(_position % _blockSize);
            int avail  = (int)Math.Min(_blockSize - within, _totalBytes - _position);
            int want   = Math.Min(destination.Length - total, avail);
            if (_physMap[block] >= 0)
            {
                long fileOff = 0x8000L + (long)_physMap[block] * _blockSize + within;
                lock (_file)
                {
                    _file.Seek(fileOff, SeekOrigin.Begin);
                    _file.ReadAtLeast(destination.Slice(total, want), want, throwOnEndOfStream: false);
                }
            }
            else
            {
                destination.Slice(total, want).Clear();
            }
            total += want;
            _position += want;
        }
        return total;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}

internal static class GcnFstWalker
{
    public static IEnumerable<IAssetNode> Walk(
        IReadOnlyList<WiiFstEntry> fst, int index, GcnDiscArchive archive)
    {
        int end = (int)fst[index].NextIndex;
        for (int i = index + 1; i < end && i < fst.Count; i = (int)fst[i].NextIndex)
        {
            WiiFstEntry e = fst[i];
            yield return e.IsDirectory
                ? new GcnFolderNode(e, fst, archive)
                : (IAssetNode)new GcnFileNode(e, archive);
        }
    }
}
