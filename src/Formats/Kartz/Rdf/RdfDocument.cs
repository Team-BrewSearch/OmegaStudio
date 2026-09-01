using Kartz.Formats.Binary;
namespace Kartz.Formats.Rdf;

internal sealed class RdfProperty
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public uint Value { get; init; }
    public RdfRecord? Record { get; init; }
    public IReadOnlyList<RdfRecord>? Records { get; init; }
    public int[]? Ids { get; init; }
}
internal sealed class RdfRecord
{
    private readonly List<RdfProperty> _properties = [];
    public IReadOnlyList<RdfProperty> Properties => _properties;
    public RdfProperty? Find(string name)
    {
        foreach (RdfProperty property in _properties)
        {
            if (property.Name.Equals(name, StringComparison.Ordinal))
            {
                return property;
            }
        }
        return null;
    }
    internal void Add(RdfProperty property)
    {
        _properties.Add(property);
    }
}
internal sealed class RdfDocument
{
    private const int HeaderSize = 8;
    private const int DictionaryHeaderSize = 12;
    private const int MaxNameLength = 128;
    private const int MaxDepth = 64;
    private readonly byte[] _blob;
    private readonly string[] _names;
    private readonly Dictionary<int, RdfRecord> _records = [];
    private RdfDocument(byte[] blob, string[] names)
    {
        _blob = blob;
        _names = names;
    }
    public RdfRecord Root { get; private set; } = new();
    public static RdfDocument? Load(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < HeaderSize)
        {
            return null;
        }
        int dictionary = (int)BinaryRead.U32(blob, 0);
        int root = (int)BinaryRead.U32(blob, 4);
        if (ReadNames(blob, dictionary) is not { } names)
        {
            return null;
        }
        RdfDocument document = new(blob, names);
        if (document.ReadRecord(root, 0) is not { } parsed)
        {
            return null;
        }
        document.Root = parsed;
        return document;
    }
    private static string[]? ReadNames(byte[] blob, int at)
    {
        if (at < 0 || at + DictionaryHeaderSize > blob.Length)
        {
            return null;
        }
        uint count = BinaryRead.U32(blob, at);
        int strings = (int)BinaryRead.U32(blob, at + 8);
        if (strings < 0 || strings + (4L * count) > blob.Length)
        {
            return null;
        }
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            int offset = (int)BinaryRead.U32(blob, strings + (i * 4));
            if (offset < 0 || offset >= blob.Length)
            {
                return null;
            }
            names[i] = BinaryRead.Name(blob, offset, MaxNameLength);
        }
        return names;
    }
    private RdfRecord? ReadRecord(int at, int depth)
    {
        if (_records.TryGetValue(at, out RdfRecord? known))
        {
            return known;
        }
        if (depth > MaxDepth || at < 0 || at + 4 > _blob.Length)
        {
            return null;
        }
        uint count = BinaryRead.U32(_blob, at);
        if (at + 4L + (8L * count) > _blob.Length)
        {
            return null;
        }
        RdfRecord record = new();
        _records[at] = record;
        for (int i = 0; i < count; i++)
        {
            int slot = at + 4 + (i * 8);
            uint key = BinaryRead.U32(_blob, slot);
            if (ReadProperty(NameAt(key >> 16), NameAt(key & 0xFFFF),
                BinaryRead.U32(_blob, slot + 4), depth) is not { } property)
            {
                return null;
            }
            record.Add(property);
        }
        return record;
    }
    private RdfProperty? ReadProperty(string name, string type, uint value, int depth)
    {
        return type switch
        {
            "resource" => ReadRecord((int)value, depth + 1) is { } child
                                ? new RdfProperty { Name = name, Type = type, Value = value, Record = child }
                                : null,
            "resource[]" => ReadRecords((int)value, depth) is { } records
                                ? new RdfProperty { Name = name, Type = type, Value = value, Records = records }
                                : null,
            "fileid" => new RdfProperty { Name = name, Type = type, Value = value, Ids = [(int)value] },
            "fileid[]" => ReadArray((int)value) is { } ids
                                ? new RdfProperty { Name = name, Type = type, Value = value, Ids = ids }
                                : null,
            "fileidlist" => ReadList((int)value) is { } listed
                                ? new RdfProperty { Name = name, Type = type, Value = value, Ids = listed }
                                : null,
            _ => new RdfProperty { Name = name, Type = type, Value = value },
        };
    }
    private List<RdfRecord>? ReadRecords(int at, int depth)
    {
        if (ReadArray(at) is not { } elements)
        {
            return null;
        }
        List<RdfRecord> records = new(elements.Length);
        foreach (int element in elements)
        {
            if (ReadRecord(element, depth + 1) is not { } record)
            {
                return null;
            }
            records.Add(record);
        }
        return records;
    }
    private int[]? ReadList(int at)
    {
        return at >= 0 && at + 4 <= _blob.Length ? ReadArray((int)BinaryRead.U32(_blob, at)) : null;
    }
    private int[]? ReadArray(int at)
    {
        if (at < 0 || at + 4 > _blob.Length)
        {
            return null;
        }
        uint count = BinaryRead.U32(_blob, at);
        if (at + 4L + (4L * count) > _blob.Length)
        {
            return null;
        }
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (int)BinaryRead.U32(_blob, at + 4 + (i * 4));
        }
        return values;
    }
    private string NameAt(uint index)
    {
        return index < (uint)_names.Length ? _names[index] : string.Empty;
    }
}
