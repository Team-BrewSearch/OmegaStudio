using System.Text;
using Omega.Plugins;
namespace PlayStation.Formats;

public sealed class Ps2DiscArchiveFormat : IArchiveFormat
{
    private const int Ps3Sector  = 1;
    private const int Pvd2Sector = 16;
    private const int SectorSize = 0x800;
    public string Id          => "playstation.ps2";
    public string DisplayName => "PlayStation disc image (PS1/PS2) (.iso)";
    public FileFilter Filter  { get; } = new("PlayStation disc image", "*.iso", "*.img");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        // Reject PS3
        if (head.Length >= (Ps3Sector * SectorSize) + 12
            && head.Slice(Ps3Sector * SectorSize, 12).SequenceEqual("PlayStation3"u8))
            return false;
        return IsPlayStationIso(path, head);
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        IsoDiscArchive archive = IsoDiscArchive.Open(path);
        host.Log($"{archive.DisplayName}: PlayStation disc, {archive.Description}.");
        return archive;
    }

    private static bool IsPlayStationIso(string path, ReadOnlySpan<byte> head)
    {
        string sysId = ReadSystemId(path, head);
        return sysId.StartsWith("PLAYSTATION", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSystemId(string path, ReadOnlySpan<byte> head)
    {
        int pvdOff = Pvd2Sector * SectorSize;
        if (head.Length >= pvdOff + 40)
        {
            if (head.Slice(pvdOff + 1).StartsWith("CD001"u8))
                return Encoding.ASCII.GetString(head.Slice(pvdOff + 8, 32)).Trim();
        }
        return IsoDiscArchive.SystemIdentifier(path);
    }
}
