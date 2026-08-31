using Omega.Plugins;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// A node backed by a slot in the archive: the base for everything the container
/// holds.
///
/// <para>
/// <see cref="BinaryAssetNode"/> supplies the whole of <see cref="IBinaryAsset"/>; all
/// this adds is where the bytes live and how a slot's row reads.
/// </para>
/// </summary>
internal abstract class SlotNode : BinaryAssetNode
{
    protected SlotNode(RingNode slot, string name, string kind = AssetKinds.Auto, string? detail = null)
        : base(name, slot.PathText, kind, detail ?? Describe(slot))
    {
        Slot = slot;
    }

    /// <summary>The archive slot this node reads and writes.</summary>
    protected RingNode Slot { get; }

    public override ReadOnlySpan<byte> Data => Slot.Span;

    public override int Size => Slot.Size;

    /// <summary>A container's bytes are derived from its children, so it cannot be replaced wholesale.</summary>
    public override bool CanReplaceBytes => !Slot.IsDirectory;

    protected override void Write(byte[] payload)
    {
        Slot.Replace(payload);
    }

    protected override string DescribeSize()
    {
        return Describe(Slot);
    }

    /// <summary>The grey text: where the slot starts and how big it is now.</summary>
    protected static string Describe(RingNode slot)
    {
        return $"0x{slot.Offset:X}  {Sizes.Describe(slot.Size)}";
    }

    /// <summary>Location rows every slot-backed node shares.</summary>
    protected IEnumerable<PropertyRow> SlotRows()
    {
        yield return PropertyRow.Of("Slot", Slot.PathText);
        yield return PropertyRow.Of("Offset", $"0x{Slot.Offset:X}");
    }
}

/// <summary>A table of contents: a slot whose children are more slots.</summary>
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

/// <summary>
/// A blob this format does not recognise.
///
/// <para>
/// Still extractable and replaceable, and still a candidate for the recognisers — a
/// standard payload sitting in one of these slots gets its pane from the Generic
/// plugin without anything here knowing what it is.
/// </para>
/// </summary>
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

/// <summary>
/// An unused slot — an offset of zero in the table of contents.
///
/// <para>
/// Shown rather than skipped so the row numbers match the file's own, which is what
/// makes a slot path in the log something you can find in a hex editor. It owns no
/// bytes, so it deliberately implements nothing.
/// </para>
/// </summary>
internal sealed class EmptySlotNode : AssetNode
{
    public EmptySlotNode(RingNode parent, int index)
        : base($"[{index:00}]", $"{parent.PathText}-{index:00}", AssetKinds.Unknown, "empty slot")
    {
    }
}
