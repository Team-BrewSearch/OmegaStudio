using System.Diagnostics.CodeAnalysis;
namespace Omega.Disc.Streams;
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "CONC-002: the name says what it is, a shared file handle; the suffix is the point of the type.")]
public sealed class SharedStream : IDisposable
{
    private readonly Lock _gate = new();
    private readonly System.IO.Stream _source;
    internal SharedStream(System.IO.Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }
    public long Length => _source.Length;
    public System.IO.Stream Stream => _source;
    public bool CanWrite => _source.CanWrite;
    public int ReadAt(long at, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        lock (_gate)
        {
            _source.Position = at;
            return _source.Read(destination);
        }
    }
    public void ReadExactlyAt(long at, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = ReadAt(at + total, destination[total..]);
            if (read <= 0)
            {
                throw new System.IO.EndOfStreamException(
                    $"reading {destination.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"byte(s) at 0x{at:X} ran out with "
                    + $"{(destination.Length - total).ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"to go, against a {_source.Length}-byte file");
            }
            total += read;
        }
    }
    public void WriteAt(long at, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        lock (_gate)
        {
            _source.Position = at;
            _source.Write(bytes);
        }
    }
    public void Flush()
    {
        lock (_gate)
        {
            _source.Flush();
            if (_source is System.IO.FileStream file && !file.SafeFileHandle.IsClosed)
            {
                file.Flush(flushToDisk: true);
            }
        }
    }
    public void Dispose()
    {
        _source.Dispose();
        GC.SuppressFinalize(this);
    }
    public static SharedStream Open(string path, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        System.IO.FileStream stream = new(
            path,
            System.IO.FileMode.Open,
            readOnly ? System.IO.FileAccess.Read : System.IO.FileAccess.ReadWrite,
            System.IO.FileShare.Read);
        return new SharedStream(stream);
    }
}
