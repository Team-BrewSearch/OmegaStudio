using System.Text;
using Omega.Disc.Formats.Nintendo;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Nintendo.Formats.Nodes;
internal sealed class WbfsRootNode : AssetNode
{
    private readonly WbfsArchive _archive;
    public WbfsRootNode(WbfsArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, $"{archive.Container.DiscCount} disc(s)")
    {
        _archive = archive;
        ChildFactory = () => _archive.Container.Slots.Select(slot => new WbfsSlotNode(_archive, slot));
    }
}
internal sealed class WbfsSlotNode : AssetNode, IDescribable
{
    private readonly WbfsArchive _archive;
    private readonly WbfsSlot _slot;
    public WbfsSlotNode(WbfsArchive archive, WbfsSlot slot)
        : base(SlotLabel(slot.Header), $"slot{slot.Slot}", AssetKinds.Container, Title(slot.Header))
    {
        _archive = archive;
        _slot = slot;
        ChildFactory = Partitions;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Game id", SlotLabel(_slot.Header));
        yield return PropertyRow.Of("Title", Title(_slot.Header));
        yield return PropertyRow.Of("Maker code", Encoding.ASCII.GetString(_slot.Header, 4, 2));
        yield return PropertyRow.Of("Clusters used", _slot.UsedSectors);
        yield return PropertyRow.Of("Cluster size", Sizes.Describe(_archive.Container.Geometry.WbfsSectorSize));
        using WiiDisc disc = Disc();
        yield return PropertyRow.Of("Partitions", disc.Partitions.Count);
        if (disc.GamePartition is { } game)
        {
            yield return PropertyRow.Of("Game partition data", Sizes.Describe(game.DecryptedSize));
            yield return PropertyRow.Of("Title id", Convert.ToHexString(game.TitleId ?? []).ToLowerInvariant());
        }
        else
        {
            yield return PropertyRow.Of("Game partition data", "none: the table lists no data partition");
        }
    }
    private IEnumerable<IAssetNode> Partitions()
    {
        using WiiDisc disc = Disc();
        foreach (WiiPartitionHeader partition in disc.Partitions)
        {
            yield return new WbfsPartitionNode(_archive, _slot, disc, partition);
        }
    }
    private WiiDisc Disc()
    {
        Stream image = _archive.Container.OpenDisc(_slot);
        return WiiDisc.Open(image);
    }
    private static string SlotLabel(byte[] header) =>
        Encoding.ASCII.GetString(header, 0, 4).Trim();
    private static string Title(byte[] header)
    {
        ReadOnlySpan<byte> title = header.AsSpan(0x20, 64);
        bool ascii = true;
        for (int i = 1; i < title.Length; i += 2)
        {
            ascii &= title[i] == 0;
        }
        return ascii
            ? Encoding.ASCII.GetString(title).TrimEnd('\0')
            : Encoding.BigEndianUnicode.GetString(title).TrimEnd('\0');
    }
}
internal sealed class WbfsPartitionNode : AssetNode, IDescribable
{
    private readonly WbfsArchive _archive;
    private readonly WbfsSlot _slot;
    private readonly WiiDisc _disc;
    private readonly WiiPartitionHeader _partition;
    private readonly Stream _image;
    public WbfsPartitionNode(
        WbfsArchive archive,
        WbfsSlot slot,
        WiiDisc disc,
        WiiPartitionHeader partition)
        : base(
            PartitionName(partition),
            $"slot{slot.Slot}/p{partition.Offset:X}",
            AssetKinds.Container,
            Sizes.Describe(partition.DecryptedSize))
    {
        _archive = archive;
        _slot = slot;
        _disc = disc;
        _partition = partition;
        _image = disc.OpenPartition(partition);
        ChildFactory = () => WiiTree.Children(_image, PartitionLabel(), _archive.MarkDirty);
    }
    private string PartitionLabel() => $"partition at 0x{_partition.Offset:X}";
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Offset in disc", $"0x{_partition.Offset:X}");
        yield return PropertyRow.Of("Data size", Sizes.Describe(_partition.DecryptedSize));
        yield return PropertyRow.Of("Encrypted", _disc.Header.DisableEncryption == 0);
        yield return PropertyRow.Of("Slot", _slot.Slot);
    }
    private static string PartitionName(WiiPartitionHeader partition)
    {
        string kind = partition.Type switch
        {
            0 => "Game",
            1 => "Update",
            2 => "Channel",
            _ => "Data",
        };
        return $"{kind} partition";
    }
}
