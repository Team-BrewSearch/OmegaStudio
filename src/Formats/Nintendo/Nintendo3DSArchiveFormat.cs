using Omega.Plugins;
namespace Nintendo.Formats;
public sealed class Nintendo3DSArchiveFormat : IArchiveFormat
{
    public string Id => "nintendo.3ds";
    public string DisplayName => "Nintendo 3DS image (.3ds, .cci)";
    public FileFilter Filter { get; } = new("Nintendo 3DS", "*.3ds", "*.cci");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x400 || !N3DS.IsNcsd(head))
        {
            return false;
        }
        try
        {
            long length = new FileInfo(path).Length;
            if (length < N3DS.MagicOffset + N3DS.MediaUnit)
            {
                return false;
            }
            NcsdHeader header = N3DS.Read(head);
            if (header.ImageSizeInMediaUnits == 0)
            {
                return false;
            }
            return header.Partitions.Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Nintendo3DSArchive archive = Nintendo3DSArchive.Open(path, host);
        host.Log($"{archive.DisplayName}: NCSD image, {archive.Partitions.Count} partition(s).");
        return archive;
    }
}
