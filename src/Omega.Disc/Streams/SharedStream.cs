using System.Diagnostics.CodeAnalysis;
namespace Omega.Disc.Streams;
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "CONC-002: the name says what it is, a shared file handle; the suffix is the point of the type.")]
public sealed class SharedStream : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string? _path;
    private System.IO.Stream _source;
    internal SharedStream(System.IO.Stream source, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _path = path;
    }
    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _source.Length;
            }
        }
    }
    public System.IO.Stream Stream => _source;
    public bool CanWrite => _source.CanWrite;
    public bool CanReopen => _path is not null;
    public string? Path => _path;
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
                    + $"to go, against a {Length}-byte file");
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
    public void Reopen(bool readOnly)
    {
        lock (_gate)
        {
            if (_source.CanWrite != readOnly)
            {
                return;
            }
            if (_path is null)
            {
                throw new System.IO.IOException(
                    "the stream was handed over open for reading only, so it cannot be reopened for writing");
            }
            _source.Dispose();
            try
            {
                _source = OpenFile(_path, readOnly);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _source = OpenFile(_path, !readOnly);
                throw new System.IO.IOException(
                    $"{System.IO.Path.GetFileName(_path)} could not be opened for "
                    + $"{(readOnly ? "reading only" : "writing")}: {ex.Message}",
                    ex);
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _source.Dispose();
        }
        GC.SuppressFinalize(this);
    }
    public static SharedStream Open(string path, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new SharedStream(OpenFile(path, readOnly), System.IO.Path.GetFullPath(path));
    }
    private static System.IO.FileStream OpenFile(string path, bool readOnly) =>
        new(
            path,
            System.IO.FileMode.Open,
            readOnly ? System.IO.FileAccess.Read : System.IO.FileAccess.ReadWrite,
            System.IO.FileShare.Read);
}
