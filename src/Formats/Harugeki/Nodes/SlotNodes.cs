using Omega.Plugins;
using System.Globalization;
namespace Harugeki.Formats.Nodes;

internal abstract class SlotNode : BinaryAssetNode
{
    protected SlotNode(
        RingNode slot,
        string name,
        string kind = AssetKinds.Auto,
        string? detail = null,
        string? stem = null)
        : base(name, slot.PathText, kind, detail ?? Describe(slot))
    {
        Slot = slot;
        FileStem = string.IsNullOrWhiteSpace(stem)
            ? slot.Index.ToString("00", CultureInfo.InvariantCulture)
            : stem;
    }
    protected RingNode Slot { get; }
    public override string FileStem { get; }
    public override ReadOnlySpan<byte> Data => Slot.Span;
    public override int Size => Slot.Size;
    public override bool CanReplaceBytes => !Slot.IsDirectory;
    protected override void Write(byte[] payload)
    {
        Slot.Replace(payload);
    }
    protected override string DescribeSize()
    {
        return Describe(Slot);
    }
    protected static string Describe(RingNode slot)
    {
        return $"0x{slot.Offset:X}  {Sizes.Describe(slot.Size)}";
    }
    protected IEnumerable<PropertyRow> SlotRows()
    {
        yield return PropertyRow.Of("Offset", $"0x{Slot.Offset:X}");
    }
}
internal sealed class ContainerNode : SlotNode, IDescribable
{
    public ContainerNode(RingNode slot, string name)
        : base(slot, name)
    {
        ChildFactory = () => NodeFactory.Children(slot);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Entries", Slot.Children.Count);
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
internal sealed class RawNode : SlotNode, IDescribable
{
    public RawNode(RingNode slot, string name)
        : base(slot, name, detail: Describe(slot) + "  raw")
    {
    }
    public IEnumerable<PropertyRow> Describe()
    {
        return SlotRows();
    }
}
internal sealed class EmptySlotNode : AssetNode
{
    public EmptySlotNode(RingNode parent, int index)
        : base($"[{index:00}]", $"{parent.PathText}-{index:00}", AssetKinds.Unknown, "empty slot")
    {
    }
}
