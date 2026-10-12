using System.Text;
using Omega.Disc.Formats.Xbox;
using Omega.Plugins;
namespace Xbox.Formats;
public sealed class XgameDiscArchiveFormat : IArchiveFormat
{
    public string Id => "xbox.xgd";
    public string DisplayName => "Xbox 360 game disc (.iso)";
    public FileFilter Filter { get; } = new("Xbox 360 game disc", "*.iso");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        try
        {
            long length = new FileInfo(path).Length;
            if (length < XgdBases.DescriptorSector * XgdBases.Sector)
            {
                return false;
            }
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return GdfxVolume.Open(stream) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        XgameDiscArchive archive = XgameDiscArchive.Open(path, host);
        host.Log($"{archive.DisplayName}: Xbox 360 game partition at 0x{archive.Volume.BaseOffset:X}, "
            + $"{archive.Volume.Entries.Count} entries.");
        return archive;
    }
}
public sealed class XgameDiscArchive : IArchive
{
    private readonly FileStream _stream;
    private XgameDiscArchive(string path, IHostServices host, FileStream stream, GdfxVolume volume)
    {
        _stream = stream;
        Host = host;
        Volume = volume;
        DisplayName = Path.GetFileName(path);
        Root = new Nodes.XgameRootNode(this, DisplayName);
    }
    public GdfxVolume Volume { get; }
    public IHostServices Host { get; }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => false;
    public IReadOnlyList<string> BackupPaths => [];
    public void Save() => throw new InvalidOperationException(
        $"{DisplayName} is open for reading, so there is nothing to write back to it.");
    public static XgameDiscArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (GdfxVolume.Open(stream) is not { } volume)
            {
                stream.Dispose();
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} carries no Xbox 360 game partition this build can read");
            }
            return new XgameDiscArchive(path, host, stream, volume);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
    public void Dispose()
    {
        _stream.Dispose();
    }
}
