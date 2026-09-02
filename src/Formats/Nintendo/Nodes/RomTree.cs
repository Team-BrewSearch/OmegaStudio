using Omega.Plugins;
using SceneGate.Ekona.Containers.Rom;
using Texim.Images;
using Yarhl.FileSystem;
using Yarhl.IO;
namespace Nintendo.Formats.Nodes;

internal static class RomTree
{
    public static IAssetNode Build(NdsRom rom, string displayName)
    {
        return new RomRootNode(rom, displayName);
    }
    public static IEnumerable<IAssetNode> Children(NdsRom rom, Node node, string path)
    {
        return node.Children.Select(child => For(rom, child, path));
    }
    private static IAssetNode For(NdsRom rom, Node node, string parentPath)
    {
        string path = $"{parentPath}/{node.Name}";
        return node.Format switch
        {
            ProgramInfo information => new ProgramInfoNode(rom, node.Name, path, information),
            Banner banner => new BannerNode(rom, node.Name, path, banner),
            IndexedPaletteImage icon => new BannerIconNode(rom, node, path, icon),
            IBinary => new RomFileNode(rom, node, path),
            NodeContainerFormat => new RomFolderNode(rom, node, path),
            _ => new RomPartNode(node.Name, path, DescribeFormat(node)),
        };
    }
    private static string DescribeFormat(Node node)
    {
        return node.Format?.GetType().Name ?? "unparsed";
    }
}
internal sealed class RomRootNode : AssetNode, IDescribable
{
    private readonly NdsRom _rom;
    public RomRootNode(NdsRom rom, string displayName)
        : base(displayName, displayName, AssetKinds.Container, rom.Information.GameTitle)
    {
        _rom = rom;
        ChildFactory = () => RomTree.Children(rom, rom.Root, displayName);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        ProgramInfo information = _rom.Information;
        yield return PropertyRow.Of("Game title", information.GameTitle);
        yield return PropertyRow.Of("Game code", information.GameCode);
        yield return PropertyRow.Of("Maker code", information.MakerCode);
        yield return PropertyRow.Of("Unit code", information.UnitCode);
        yield return PropertyRow.Of("Region", information.Region);
        yield return PropertyRow.Of("ROM version", information.Version);
        yield return PropertyRow.Of("Cartridge size", Sizes.Describe(information.CartridgeSize));
        yield return PropertyRow.Of("Files", _rom.CountFiles());
    }
}
internal sealed class RomFolderNode : AssetNode
{
    public RomFolderNode(NdsRom rom, Node node, string path)
        : base(node.Name, path, AssetKinds.Container, $"{node.Children.Count} entries")
    {
        ChildFactory = () => RomTree.Children(rom, node, path);
    }
}
internal sealed class RomFileNode : BinaryAssetNode
{
    private readonly NdsRom _rom;
    private readonly Node _node;
    private byte[]? _data;
    public RomFileNode(NdsRom rom, Node node, string path)
        : base(node.Name, path, AssetKinds.Auto, Sizes.Describe(node.Stream?.Length ?? 0))
    {
        _rom = rom;
        _node = node;
    }
    public override ReadOnlySpan<byte> Data => _data ??= Read();
    public override string Extension =>
        Path.GetExtension(Name) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    protected override void Write(byte[] payload)
    {
        _ = _node.ChangeFormat(new BinaryFormat(DataStreamFactory.FromArray(payload, 0, payload.Length)));
        _data = payload;
        _rom.MarkChanged();
    }
    private byte[] Read()
    {
        DataStream stream = _node.Stream
            ?? throw new InvalidDataException($"'{Name}' holds no data to read.");
        if (stream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"'{Name}' claims {stream.Length} bytes, more than this host can hold in one node.");
        }
        byte[] buffer = new byte[stream.Length];
        stream.Position = 0;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
internal sealed class RomPartNode : AssetNode
{
    public RomPartNode(string name, string path, string detail)
        : base(name, path, AssetKinds.Unknown, detail)
    {
    }
}
