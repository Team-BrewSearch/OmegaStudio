using System.Globalization;
using Omega.Disc.Formats.Nintendo;
using Omega.Disc.IO;
using Omega.Disc.Streams;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace Nintendo.Formats.Nodes;
internal static class WiiTree
{
    public static IEnumerable<IAssetNode> Children(Stream data, string label, Action? onEdit = null)
    {
        byte[] boot = new byte[0x440];
        data.Position = 0;
        data.ReadExactly(boot);
        SpanReader reader = SpanReader.At(boot, 0x424);
        long fstOffset = (long)reader.UInt32Big() << WiiDisc.OffsetShift;
        long fstSize = (long)reader.UInt32Big() << WiiDisc.OffsetShift;
        if (fstSize > WiiDisc.MaxFileSystemSize)
        {
            throw new InvalidDataException(
                $"the {label} declares a {fstSize.ToString(CultureInfo.InvariantCulture)}-byte file system table, "
                + $"past the {WiiDisc.MaxFileSystemSize.ToString(CultureInfo.InvariantCulture)} bytes a Wii could load");
        }
        if (fstSize < 0x18 || fstOffset + fstSize > data.Length)
        {
            throw new InvalidDataException(
                $"the {label} puts a {fstSize.ToString(CultureInfo.InvariantCulture)}-byte file system table at "
                + $"0x{fstOffset:X} inside a {data.Length.ToString(CultureInfo.InvariantCulture)}-byte data area, "
                + "which no partition holds. The partition's data is either encrypted with a key this build does "
                + "not carry or is not Wii partition data.");
        }
        data.Position = fstOffset;
        byte[] table = new byte[fstSize];
        data.ReadExactly(table);
        IReadOnlyList<WiiFstEntry> entries = WiiDisc.ReadFileSystemTable(table);
        foreach (WiiFstEntry entry in entries)
        {
            if (entry.IsDirectory && (entry.NextIndex <= entry.Index || entry.NextIndex > entries.Count))
            {
                throw new InvalidDataException(
                    $"entry {entry.Index.ToString(CultureInfo.InvariantCulture)} of the {label} is a directory "
                    + $"that ends at table index {entry.NextIndex.ToString(CultureInfo.InvariantCulture)}, outside "
                    + $"the {entries.Count.ToString(CultureInfo.InvariantCulture)} entries the table holds");
            }
        }
        long wanted = 0;
        foreach (WiiFstEntry entry in entries)
        {
            if (!entry.IsDirectory)
            {
                wanted = Math.Max(wanted, entry.Offset + entry.Length);
            }
        }
        if (data is WiiPartitionStream partition && wanted > data.Length)
        {
            partition.EnsureDataLength(wanted);
        }
        return Walk(entries, 0, data, onEdit);
    }
    private static IEnumerable<IAssetNode> Walk(
        IReadOnlyList<WiiFstEntry> entries,
        int index,
        Stream data,
        Action? onEdit)
    {
        int end = (int)entries[index].NextIndex;
        for (int i = index + 1; i < end && i < entries.Count; i = (int)entries[i].NextIndex)
        {
            WiiFstEntry entry = entries[i];
            yield return entry.IsDirectory
                ? new WiiFolderNode(entries, i, entry, data, onEdit)
                : new WiiFileNode(entries, i, entry, data, onEdit);
        }
    }
}
internal sealed class WiiFolderNode : AssetNode
{
    private readonly IReadOnlyList<WiiFstEntry> _entries;
    private readonly int _index;
    private readonly Stream _data;
    public WiiFolderNode(
        IReadOnlyList<WiiFstEntry> entries,
        int index,
        WiiFstEntry entry,
        Stream data,
        Action? onEdit)
        : base(entry.Name, entry.Name, AssetKinds.Container, "")
    {
        _entries = entries;
        _index = index;
        _data = data;
        ChildFactory = () =>
        {
            List<IAssetNode> children = [];
            int end = (int)entry.NextIndex;
            for (int i = _index + 1; i < end && i < _entries.Count; i = (int)_entries[i].NextIndex)
            {
                WiiFstEntry child = _entries[i];
                children.Add(child.IsDirectory
                    ? new WiiFolderNode(_entries, i, child, _data, onEdit)
                    : new WiiFileNode(_entries, i, child, _data, onEdit));
            }
            return children;
        };
    }
}
internal sealed class WiiFileNode : StreamingAssetNode
{
    private readonly Stream _data;
    private readonly long _offset;
    private readonly Action? _onEdit;
    private byte[]? _edited;
    public WiiFileNode(
        IReadOnlyList<WiiFstEntry> entries,
        int index,
        WiiFstEntry entry,
        Stream data,
        Action? onEdit = null)
        : base(
            entry.Name,
            entry.Name,
            new PartitionWindow(data, entry.Offset, entry.Length),
            AssetKinds.Auto,
            Sizes.Describe((long)entry.Length))
    {
        _data = data;
        _offset = entry.Offset;
        _onEdit = onEdit;
    }
    public override string Extension => Path.GetExtension(Name) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    public override bool CanReplaceBytes => _data is WiiPartitionStream { ImageCanWrite: true };
    public override ReadOnlySpan<byte> Data
    {
        get
        {
            return _edited ?? Source.ReadAll();
        }
    }
    protected override void Write(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        long length = Source.Length;
        if (payload.Length != length)
        {
            throw new InvalidDataException(
                $"'{Name}' is {length} bytes long in this partition, and the replacement is {payload.Length}. "
                + "A Wii partition has no room to grow, so give a payload of the same length.");
        }
        ((WiiPartitionStream)_data).Patch(_offset, payload);
        _edited = payload;
        _onEdit?.Invoke();
    }
    private sealed class PartitionWindow(Stream data, long offset, uint length) : IPayloadSource
    {
        public long Length => length;
        public int Head(Span<byte> destination)
        {
            int count = (int)Math.Min(destination.Length, length);
            if (count <= 0)
            {
                return 0;
            }
            data.Position = offset;
            int read = data.Read(destination[..count]);
            return read < 0 ? 0 : read;
        }
        public byte[] ReadAll()
        {
            if (length == 0)
            {
                return [];
            }
            if (length > Array.MaxLength || offset + length > data.Length)
            {
                throw new InvalidDataException(
                    $"a {length.ToString(CultureInfo.InvariantCulture)}-byte file at 0x{offset:X} runs past the "
                    + $"{data.Length.ToString(CultureInfo.InvariantCulture)}-byte partition data, or past what one "
                    + "node can hold");
            }
            byte[] payload = new byte[length];
            data.Position = offset;
            data.ReadExactly(payload);
            return payload;
        }
    }
}
