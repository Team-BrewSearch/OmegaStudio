using Omega.Disc.Streams;
using Omega.Plugins;
namespace Nintendo.Formats;
public sealed class Nintendo3DSArchive : IArchive
{
    private readonly FileStream _stream;
    private Nintendo3DSArchive(string path, IHostServices host, FileStream stream)
    {
        _stream = stream;
        Host = host;
        DisplayName = Path.GetFileName(path);
        Root = new Nodes.ThreeDsRootNode(this, DisplayName);
    }
    public List<(int Slot, long Offset, long Length)> Partitions { get; } = [];
    public long ImageLength => _stream.Length;
    public NcsdHeader Header { get; private init; } = null!;
    public IHostServices Host { get; }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => false;
    public IReadOnlyList<string> BackupPaths => [];
    public void Save() => throw new InvalidOperationException(
        $"{DisplayName} is open for reading, so there is nothing to write back to it.");
    public static Nintendo3DSArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (N3DS.Read(stream) is not { } header || !header.Partitions.Any())
            {
                stream.Dispose();
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} carries no NCSD header with a partition table this build can read");
            }
            Nintendo3DSArchive archive = new(path, host, stream) { Header = header };
            archive.Partitions.AddRange(header.Partitions);
            return archive;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
    public Stream Range(long offset, long length)
    {
        return new WindowStream(_stream, offset, length);
    }
    public void Dispose()
    {
        _stream.Dispose();
    }
}
