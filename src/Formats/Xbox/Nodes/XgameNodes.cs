using Omega.Disc.Formats.Xbox;
using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Xbox.Formats.Nodes;
internal sealed class XgameRootNode : AssetNode
{
    private readonly XgameDiscArchive _archive;
    public XgameRootNode(XgameDiscArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, $"{archive.Volume.Entries.Count} entries")
    {
        _archive = archive;
        ChildFactory = () => Children(archive.Volume.RootSector, archive.Volume.RootSize);
    }
    private List<IAssetNode> Children(uint tableSector, uint tableSize)
    {
        List<IAssetNode> rows = [];
        foreach (GdfxEntry entry in _archive.Volume.Table(tableSector, tableSize))
        {
            rows.Add(entry.IsDirectory
                ? new XgameDirectoryNode(_archive, entry)
                : new XgameFileNode(_archive, entry));
        }
        return rows;
    }
}
internal sealed class XgameDirectoryNode : AssetNode
{
    private readonly XgameDiscArchive _archive;
    private readonly GdfxEntry _entry;
    public XgameDirectoryNode(XgameDiscArchive archive, GdfxEntry entry)
        : base(entry.Name, entry.Name, AssetKinds.Container, Sizes.Describe(entry.Length))
    {
        _archive = archive;
        _entry = entry;
        ChildFactory = () =>
        {
            List<IAssetNode> rows = [];
            foreach (GdfxEntry child in _archive.Volume.Table(_entry.FirstSector, _entry.Length))
            {
                rows.Add(child.IsDirectory
                    ? new XgameDirectoryNode(_archive, child)
                    : new XgameFileNode(_archive, child));
            }
            return rows;
        };
    }
}
internal sealed class XgameFileNode : StreamingAssetNode
{
    private readonly string _label;
    public XgameFileNode(XgameDiscArchive archive, GdfxEntry entry)
        : base(
            entry.Name,
            entry.Name,
            new ClusterWindow(archive, entry),
            AssetKinds.Auto,
            Sizes.Describe(entry.Length))
    {
        _label = entry.Name;
    }
    public override string Extension => Path.GetExtension(Name) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    public override bool CanReplaceBytes => false;
    protected override void Write(byte[] payload)
    {
        throw new InvalidOperationException(
            $"'{_label}' is read only: an Xbox 360 game image cannot take a replacement of a different length, "
            + "and this one is open for reading.");
    }
    private sealed class ClusterWindow(XgameDiscArchive archive, GdfxEntry entry) : IPayloadSource
    {
        public long Length => entry.Length;
        public int Head(Span<byte> destination)
        {
            if (Length == 0)
            {
                return 0;
            }
            using Stream stream = archive.Volume.OpenRead(entry);
            return stream.Read(destination);
        }
        public byte[] ReadAll()
        {
            if (Length == 0)
            {
                return [];
            }
            using Stream stream = archive.Volume.OpenRead(entry);
            return PayloadReader.ReadAll(stream, entry.Name);
        }
    }
}
