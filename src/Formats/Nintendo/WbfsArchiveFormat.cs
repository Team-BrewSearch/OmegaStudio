using System.Buffers.Binary;
using System.Text;
using Omega.Disc.Formats.Nintendo;
using Omega.Plugins;
namespace Nintendo.Formats;
public sealed class WbfsArchiveFormat : IArchiveFormat
{
    public string Id => "nintendo.wbfs";
    public string DisplayName => "WBFS container (.wbfs)";
    public FileFilter Filter { get; } = new("WBFS container", "*.wbfs");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x10 || BinaryPrimitives.ReadUInt32BigEndian(head) != 0x57424653)
        {
            return false;
        }
        int hdShift = head[8];
        int wbfsShift = head[9];
        if (hdShift is < 9 or > 20 || wbfsShift <= hdShift)
        {
            return false;
        }
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        uint sectors = BinaryPrimitives.ReadUInt32BigEndian(head[4..8]);
        return (1L << hdShift) * sectors <= (length * 1.01) + 1;
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        WbfsArchive archive = WbfsArchive.Open(path, host);
        WbfsContainer container = archive.Container;
        host.Log($"{archive.DisplayName}: WBFS, {container.DiscCount} disc(s), "
            + $"{1 << container.Geometry.HdSectorShift}-byte sectors, "
            + $"{container.FreeSectors} of {container.Geometry.TotalWbfsSectors} clusters free.");
        foreach (WbfsSlot slot in container.Slots)
        {
            host.Log($"  slot {slot.Slot}: {Label(slot.Header)}, {slot.UsedSectors} clusters.");
        }
        return archive;
    }
    private static string Label(byte[] header)
    {
        string gameId = Encoding.ASCII.GetString(header, 0, 4).Trim();
        string title = Encoding.BigEndianUnicode.GetString(header, 0x20, 64).TrimEnd('\0');
        return string.IsNullOrEmpty(title) ? gameId : $"{gameId} — {title}";
    }
}
