using System.Globalization;
using System.Text;
using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Nintendo.Formats.Nodes;
internal sealed class NcchPartitionNode : AssetNode, IDescribable
{
    private readonly Nintendo3DSArchive _archive;
    private readonly int _slot;
    private readonly long _offset;
    private readonly long _length;
    private readonly Ncch _ncch;
    public NcchPartitionNode(Nintendo3DSArchive archive, int slot, long offset, long length, Ncch ncch)
        : base($"Partition {slot}", $"p{slot}", AssetKinds.Container, ncch.Header.ProductCode)
    {
        _archive = archive;
        _slot = slot;
        _offset = offset;
        _length = length;
        _ncch = ncch;
        ChildFactory = Children;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Offset in image", $"0x{_offset:X}");
        yield return PropertyRow.Of("Length", Sizes.Describe(_length));
        NcsdHeader header = _archive.Header;
        if (_slot < header.CryptType.Length)
        {
            yield return PropertyRow.Of("Crypt type", header.CryptType[_slot].ToString(CultureInfo.InvariantCulture));
        }
        if (_slot < header.FsType.Length)
        {
            yield return PropertyRow.Of("Filesystem type", FsName(header.FsType[_slot]));
        }
        yield return PropertyRow.Of("Product code", _ncch.Header.ProductCode);
        yield return PropertyRow.Of("Maker code", _ncch.Header.MakerCode);
        yield return PropertyRow.Of("ExeFS", _ncch.ExeFs is null
            ? "not readable"
            : $"{_ncch.ExeFs.Files.Count} file(s)");
        yield return PropertyRow.Of("RomFS", _ncch.RomFs is null
            ? "not readable"
            : $"{_ncch.RomFs.DirectoryCount} folder(s), {_ncch.RomFs.FileCount} file(s)");
        if (_ncch.Note is { } note)
        {
            yield return PropertyRow.Of("Note", note);
        }
    }
    private List<IAssetNode> Children()
    {
        List<IAssetNode> rows = [];
        if (_ncch.ExeFs is { } exeFs)
        {
            rows.Add(new ExeFsNode(_archive, _ncch, exeFs));
        }
        if (_ncch.RomFs is { } romFs)
        {
            rows.Add(new RomFsNode(_archive, _ncch, romFs, romFs.Root));
        }
        if (rows.Count == 0)
        {
            rows.Add(new UnreadableNode(_ncch.Note ?? "no filesystems could be read"));
        }
        return rows;
    }
    private static string FsName(byte type) => type switch
    {
        0 => "none",
        1 => "normal",
        3 => "FIRM",
        4 => "AGB_FIRM save",
        _ => type.ToString(CultureInfo.InvariantCulture),
    };
}
internal sealed class UnreadableNode : AssetNode
{
    public UnreadableNode(string note)
        : base("Files", "files", AssetKinds.Container, note)
    {
    }
}
internal sealed class ExeFsNode : AssetNode
{
    private readonly Nintendo3DSArchive _archive;
    private readonly Ncch _ncch;
    private readonly ExeFs _exeFs;
    public ExeFsNode(Nintendo3DSArchive archive, Ncch ncch, ExeFs exeFs)
        : base("ExeFS", "exefs", AssetKinds.Container, $"{exeFs.Files.Count} file(s)")
    {
        _archive = archive;
        _ncch = ncch;
        _exeFs = exeFs;
        ChildFactory = () => _exeFs.Files
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => (IAssetNode)new NcchFileNode(
                _archive, file.Name, file.FileOffset, file.FileLength, $"exefs/{file.Name}"))
            .ToList();
    }
}
internal sealed class RomFsNode : AssetNode, IDescribable
{
    private readonly Nintendo3DSArchive _archive;
    private readonly Ncch _ncch;
    private readonly RomFs _romFs;
    private readonly RomFsDirectory _root;
    public RomFsNode(Nintendo3DSArchive archive, Ncch ncch, RomFs romFs, RomFsDirectory root)
        : base("RomFS", "romfs", AssetKinds.Container,
            $"{romFs.DirectoryCount} folder(s), {romFs.FileCount} file(s)")
    {
        _archive = archive;
        _ncch = ncch;
        _romFs = romFs;
        _root = root;
        ChildFactory = Children;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Folders", _romFs.DirectoryCount);
        yield return PropertyRow.Of("Files", _romFs.FileCount);
        yield return PropertyRow.Of("Data offset", $"0x{_romFs.DataStart:X}");
    }
    private List<IAssetNode> Children()
    {
        List<IAssetNode> rows = [];
        foreach (RomFsFile file in _romFs.Files(_root.FirstFile))
        {
            rows.Add(new NcchFileNode(
                _archive, file.Name, _romFs.DataStart + file.DataOffset, (long)file.Length, file.Name));
        }
        foreach (RomFsDirectory directory in _romFs.Directories(_root.FirstChild))
        {
            rows.Add(new RomFsFolderNode(_archive, _romFs, directory));
        }
        rows.Sort((left, right) =>
            left.Kind != right.Kind
                ? (left.Kind == AssetKinds.Container ? -1 : 1)
                : string.CompareOrdinal(left.Name, right.Name));
        return rows;
    }
}
internal sealed class RomFsFolderNode : AssetNode
{
    private readonly Nintendo3DSArchive _archive;
    private readonly RomFs _romFs;
    private readonly RomFsDirectory _directory;
    public RomFsFolderNode(Nintendo3DSArchive archive, RomFs romFs, RomFsDirectory directory)
        : base(directory.Name, directory.Name, AssetKinds.Container, "")
    {
        _archive = archive;
        _romFs = romFs;
        _directory = directory;
        ChildFactory = Children;
    }
    private List<IAssetNode> Children()
    {
        List<IAssetNode> rows = [];
        foreach (RomFsFile file in _romFs.Files(_directory.FirstFile))
        {
            rows.Add(new NcchFileNode(
                _archive, file.Name, _romFs.DataStart + file.DataOffset, (long)file.Length, file.Name));
        }
        foreach (RomFsDirectory child in _romFs.Directories(_directory.FirstChild))
        {
            rows.Add(new RomFsFolderNode(_archive, _romFs, child));
        }
        rows.Sort((left, right) =>
            left.Kind != right.Kind
                ? (left.Kind == AssetKinds.Container ? -1 : 1)
                : string.CompareOrdinal(left.Name, right.Name));
        return rows;
    }
}
internal sealed class NcchFileNode : StreamingAssetNode
{
    public NcchFileNode(Nintendo3DSArchive archive, string name, long offset, long length, string path)
        : base(name, path, new ImageWindow(archive, name, offset, length), AssetKinds.Auto, Sizes.Describe(length))
    {
    }
    public override string Extension => Path.GetExtension(Name) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    public override bool CanReplaceBytes => false;
    protected override void Write(byte[] payload)
    {
        throw new InvalidOperationException(
            $"'{Name}' is read only: a 3DS image's filesystems are read through their own tables, "
            + "and this build does not rewrite them.");
    }
    private sealed class ImageWindow(Nintendo3DSArchive archive, string name, long offset, long length) : IPayloadSource
    {
        public long Length => length;
        public int Head(Span<byte> destination)
        {
            if (length == 0)
            {
                return 0;
            }
            using Stream window = archive.Range(offset, Math.Min(length, destination.Length));
            return window.Read(destination);
        }
        public byte[] ReadAll()
        {
            if (length == 0)
            {
                return [];
            }
            using Stream window = archive.Range(offset, length);
            return PayloadReader.ReadAll(window, name);
        }
    }
}
