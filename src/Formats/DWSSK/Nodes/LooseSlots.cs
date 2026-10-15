using DWSSK.Formats.Core;
using Omega.Plugins;

namespace DWSSK.Formats.Nodes;

internal sealed class LooseSlot(string file, Platform? platform, int size, Func<byte[]> load, Action<byte[]>? sink) : Slot
{
    private byte[]? _data;
    private int _revision;

    public LooseGroup Owner { get; set; } = null!;
    public bool IsDirty { get; private set; }
    public override string File => file;
    public override Platform Platform => platform ?? Owner.Platform;
    public override bool PlatformKnown => platform is not null;
    public override byte[] Data => _data ??= load();
    public override int Size => _data?.Length ?? size;
    public override bool Loaded => _data is not null;
    public override int Revision => _revision;
    public override bool CanWrite => sink is not null;
    public override uint? Hash => null;
    public override SlotGroup Group => Owner;

    public override void Replace(byte[] data)
    {
        if (sink is null)
        {
            throw new InvalidOperationException($"{file} cannot be written back");
        }
        _data = data;
        _revision++;
        IsDirty = true;
        sink(data);
        Owner.Touch();
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }
}

internal sealed class LooseGroup : SlotGroup
{
    private readonly IReadOnlyList<LooseSlot> _slots;
    private int _revision;

    public LooseGroup(IHostServices host, IReadOnlyList<LooseSlot> slots)
        : base(host)
    {
        _slots = slots;
        foreach (LooseSlot s in slots)
        {
            s.Owner = this;
        }
    }

    public override Platform Platform => _slots.FirstOrDefault(s => s.PlatformKnown)?.Platform ?? Platform.PS3;
    public override int Revision => _revision;

    public void Touch()
    {
        _revision++;
    }

    public override IEnumerable<Slot> All()
    {
        return _slots;
    }

    public override IEnumerable<Slot> Parts(Slot from)
    {
        return _slots;
    }
}

internal sealed class ChildSlot(Slot parent, string inner, Func<byte[]> read, Func<byte[], byte[]>? inject) : Slot
{
    private (int Revision, byte[] Data)? _cache;

    public Slot Parent { get; } = parent;
    public string Inner { get; } = inner;
    public override string File => $"{Parent.File}/{Inner}";
    public override Platform Platform => Parent.Platform;
    public override bool PlatformKnown => Parent.PlatformKnown;
    public override int Revision => Parent.Revision;
    public override bool CanWrite => inject is not null && Parent.CanWrite;
    public override uint? Hash => null;
    public override SlotGroup Group => Parent.Group;

    public override byte[] Data
    {
        get
        {
            if (_cache is not { } c || c.Revision != Parent.Revision)
            {
                _cache = c = (Parent.Revision, read());
            }
            return c.Data;
        }
    }

    public override void Replace(byte[] data)
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException($"{File} cannot be written back");
        }
        Parent.Replace(inject!(data));
    }
}
