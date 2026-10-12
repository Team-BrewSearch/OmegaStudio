using System.Text;
using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Nintendo.Formats.Nodes;
internal sealed class ThreeDsRootNode : AssetNode, IDescribable
{
    private readonly Nintendo3DSArchive _archive;
    public ThreeDsRootNode(Nintendo3DSArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, $"{archive.Partitions.Count} partition(s)")
    {
        _archive = archive;
        ChildFactory = () => _archive.Partitions
            .Select(partition => new NcchPartitionNode(
                _archive, partition.Slot, partition.Offset, partition.Length, NcchFor(partition)))
            .ToList();
    }
    public IEnumerable<PropertyRow> Describe()
    {
        NcsdHeader header = _archive.Header;
        yield return PropertyRow.Of("Format", "NCSD");
        yield return PropertyRow.Of("Image size", Sizes.Describe(header.ImageSizeInMediaUnits * 0x200L));
        yield return PropertyRow.Of("Media id", Convert.ToHexString(header.MediaId).ToLowerInvariant());
        yield return PropertyRow.Of("Partition slots", _archive.Partitions.Count);
        foreach (var slot in _archive.Partitions)
        {
            yield return PropertyRow.Of(
                $"Slot {SlotName(slot.Slot)}",
                $"0x{slot.Offset:X8}, {Sizes.Describe(slot.Length)}, {CryptoName(header, slot.Slot)}");
        }
    }
    private Ncch NcchFor((int Slot, long Offset, long Length) partition)
    {
        Ncch? ncch = NcchReader.Read(_archive, partition.Offset, partition.Length);
        if (ncch is null)
        {
            throw new InvalidDataException(
                $"the partition at 0x{partition.Offset:X} carries no NCCH header this build can read");
        }
        return ncch;
    }
    private static string SlotName(int slot) => slot switch
    {
        0 => "0 (game)",
        1 => "1 (manual)",
        2 => "2 (download play)",
        _ => slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    private static string CryptoName(NcsdHeader header, int slot)
    {
        if (slot < 0 || slot >= header.CryptType.Length)
        {
            return "unknown";
        }
        return header.CryptType[slot] switch
        {
            0 => "plain",
            1 => "fixed key",
            2 => "encrypted",
            _ => "encrypted",
        };
    }
}
