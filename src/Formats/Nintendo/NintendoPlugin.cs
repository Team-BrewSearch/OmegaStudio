using Nintendo.Formats;
using Nintendo.Formats.Nodes;
using Omega.Plugins;
using System.Buffers.Binary;
[assembly: OmegaPlugin(typeof(NintendoPlugin))]
namespace Nintendo.Formats;

public sealed class NintendoPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "nintendo",
        DisplayName = "Nintendo DS",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Files, header and banner of Nintendo DS ROM images, read and written with SceneGate Ekona.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new NdsRomFormat()];
}
public sealed class NdsRomFormat : IArchiveFormat
{
    private const int Arm9Offset = 0x20;
    private const int Arm9Size = 0x2C;
    private const int Arm7Offset = 0x30;
    private const int Arm7Size = 0x3C;
    private const int FatOffset = 0x48;
    private const int FatSize = 0x4C;
    private const int UsedRomSize = 0x80;
    private const int HeaderSizeField = 0x84;
    private const int MinimumHead = 0x200;
    private const uint DeclaredHeaderSize = 0x4000;
    private const uint FirstSectionStart = 0x200;
    private const int FatEntrySize = 8;
    public string Id => "nintendo.nds";
    public string DisplayName => "Nintendo DS ROM (.nds)";
    public FileFilter Filter { get; } = new("Nintendo DS ROM", "*.nds", "*.srl");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        string extension = Path.GetExtension(path);
        if (head.Length < MinimumHead
            || (!extension.Equals(".nds", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".srl", StringComparison.OrdinalIgnoreCase)))
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
        long fat = Field(head, FatOffset);
        long fatLength = Field(head, FatSize);
        return Field(head, HeaderSizeField) == DeclaredHeaderSize
            && Field(head, UsedRomSize) <= length
            && Section(head, Arm9Offset, Arm9Size, length)
            && Section(head, Arm7Offset, Arm7Size, length)
            && fatLength > 0
            && fatLength % FatEntrySize == 0
            && fat >= FirstSectionStart
            && fat + fatLength <= length;
    }
    private static bool Section(ReadOnlySpan<byte> head, int offsetField, int sizeField, long length)
    {
        long offset = Field(head, offsetField);
        long size = Field(head, sizeField);
        return size > 0 && offset >= FirstSectionStart && offset + size <= length;
    }
    private static long Field(ReadOnlySpan<byte> head, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(head[offset..]);
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        NdsRom rom = NdsRom.Load(path);
        host.Log($"{Path.GetFileName(path)}: {rom.Information.GameTitle} " +
                 $"[{rom.Information.GameCode}], {rom.CountFiles()} file(s).");
        return new NdsRomArchive(rom, path);
    }
}
internal sealed class NdsRomArchive : IArchive
{
    private readonly NdsRom _rom;
    private readonly string _path;
    public NdsRomArchive(NdsRom rom, string path)
    {
        _rom = rom;
        _path = path;
        DisplayName = Path.GetFileName(path);
        Root = RomTree.Build(rom, DisplayName);
    }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _rom.HasChanges;
    public IReadOnlyList<string> BackupPaths => [_path];
    public void Save()
    {
        _rom.Save(_path);
    }
    public void Dispose()
    {
        _rom.Dispose();
    }
}
