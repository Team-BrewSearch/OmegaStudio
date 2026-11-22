using System.Text;
using DiscUtils.Iso9660;
using Omega.Plugins;
namespace PlayStation.Formats;

public sealed class PspIsoArchiveFormat : IArchiveFormat
{
    private const int SectorSize = 0x800;
    private const int PvdSector  = 16;
    public string Id          => "playstation.psp.iso";
    public string DisplayName => "PSP disc image (.iso)";
    public FileFilter Filter  { get; } = new("PSP disc image", "*.iso");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        // Must be ISO 9660
        int pvdOff = PvdSector * SectorSize;
        bool hasIso;
        if (head.Length >= pvdOff + 6)
            hasIso = head[(pvdOff + 1)..].StartsWith("CD001"u8);
        else
            hasIso = IsoDiscArchive.HasIso9660(path);
        if (!hasIso) return false;

        // Must NOT be PS3
        if (head.Length >= (1 * SectorSize) + 12
            && head.Slice(1 * SectorSize, 12).SequenceEqual("PlayStation3"u8))
            return false;

        string sysId = pvdOff + 40 <= head.Length
            ? Encoding.ASCII.GetString(head.Slice(pvdOff + 8, 32)).Trim()
            : IsoDiscArchive.SystemIdentifier(path);

        // PSP discs often have blank or non-PLAYSTATION system ID; detect by filesystem
        if (sysId.StartsWith("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
            return false; // PS1/PS2 → let Ps2DiscArchiveFormat handle it

        return HasPspGameDir(path);
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        IsoDiscArchive archive = IsoDiscArchive.Open(path);
        host.Log($"{archive.DisplayName}: PSP UMD, {archive.Description}.");
        return archive;
    }

    private static bool HasPspGameDir(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            CDReader reader = new(fs, true);
            return reader.DirectoryExists("\\PSP_GAME");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}
