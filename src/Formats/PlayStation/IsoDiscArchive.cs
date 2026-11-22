using System.Text;
using DiscUtils.Iso9660;
using Omega.Plugins;
namespace PlayStation.Formats;

internal sealed class IsoDiscArchive : IArchive
{
    private readonly Stream _stream;
    private IsoDiscArchive(string path, Stream stream, CDReader reader, string description)
    {
        _stream     = stream;
        Reader      = reader;
        Description = description;
        DisplayName = Path.GetFileName(path);
        Root        = new Nodes.IsoRootNode(this, DisplayName);
    }
    internal CDReader Reader    { get; }
    internal Lock     Gate      { get; } = new();
    public   string   Description { get; }
    public   string   DisplayName { get; }
    public   IAssetNode Root      { get; }
    public   bool     IsDirty     => false;
    public   IReadOnlyList<string> BackupPaths => [];
    public   void Save()    => throw new InvalidOperationException($"{DisplayName} is read-only.");
    public   void Dispose() { Reader.Dispose(); _stream.Dispose(); }

    public static IsoDiscArchive Open(string path)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            CDReader reader = new(stream, true);
            string desc = ReadDescription(reader);
            return new IsoDiscArchive(path, stream, reader, desc);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static IsoDiscArchive OpenOnStream(string displayPath, Stream underlying)
    {
        CDReader reader = new(underlying, true);
        string desc = ReadDescription(reader);
        return new IsoDiscArchive(displayPath, underlying, reader, desc);
    }

    private static string ReadDescription(CDReader reader)
    {
        string volume = reader.VolumeLabel?.Trim() ?? "";
        return string.IsNullOrEmpty(volume) ? "ISO 9660" : volume;
    }

    public static string SystemIdentifier(string path)
    {
        try
        {
            const int pvdSector = 16;
            const int sectorSize = 0x800;
            byte[] buf = new byte[sectorSize];
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < (pvdSector + 1) * sectorSize) return "";
            fs.Seek(pvdSector * sectorSize, SeekOrigin.Begin);
            fs.ReadExactly(buf);
            return Encoding.ASCII.GetString(buf, 8, 32).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public static bool HasIso9660(string path)
    {
        try
        {
            const int pvdSector = 16;
            byte[] buf = new byte[6];
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < (pvdSector + 1) * 0x800) return false;
            fs.Seek(pvdSector * 0x800 + 1, SeekOrigin.Begin);
            fs.ReadExactly(buf);
            return buf.AsSpan(0, 5).SequenceEqual("CD001"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
