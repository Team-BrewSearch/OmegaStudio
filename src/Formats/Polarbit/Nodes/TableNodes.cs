using Omega.Plugins;
namespace Polarbit.Formats.Nodes;
internal sealed class LangNode : EntryNode
{
    public LangNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Text, Label(LangTable.Parse(entry.Payload)))
    {
        ChildFactory = () =>
        {
            LangTable table = LangTable.Parse(Entry.Payload);
            return Enumerable.Range(0, table.Languages.Count).Select(i => (IAssetNode)new LanguageNode(Entry, i, table.Languages[i].Name, $"{PathText}/{table.Languages[i].Name}", table.StringsPerLanguage));
        };
    }
    private static string Label(LangTable table)
    {
        return $"{table.Languages.Count} languages x {table.StringsPerLanguage} strings";
    }
    protected override void Check(byte[] payload)
    {
        _ = LangTable.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        LangTable table = LangTable.Parse(Entry.Payload);
        yield return PropertyRow.Of("Encoding", table.Utf16 ? "UTF-16LE" : "UTF-8");
        yield return PropertyRow.Of("Languages", string.Join(", ", table.Languages.Select(x => x.Name)));
        yield return PropertyRow.Of("Strings per language", table.StringsPerLanguage);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class LanguageNode(VfsEntry entry, int language, string name, string path, int count)
    : AssetNode(name, path, AssetKinds.Text, $"{count} strings"), IDescribable
{
    public IEnumerable<PropertyRow> Describe()
    {
        string[] strings = LangTable.Parse(entry.Payload).Languages[language].Strings;
        for (int k = 0; k < strings.Length; k++)
        {
            int index = k;
            yield return PropertyRow.Editable($"#{k:000}", strings[k], value => Set(index, value));
        }
    }
    private void Set(int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A string cannot contain a NUL character; the table uses it as the separator.");
        }
        LangTable table = LangTable.Parse(entry.Payload);
        table.Languages[language].Strings[index] = value;
        entry.Replace(table.Build());
    }
}
internal sealed class FcolNode : EntryNode
{
    public FcolNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Text, $"{FcolTable.Parse(entry.Payload).Strings.Count} strings")
    {
    }
    protected override void Check(byte[] payload)
    {
        _ = FcolTable.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        FcolTable table = FcolTable.Parse(Entry.Payload);
        for (int i = 0; i < table.Strings.Count; i++)
        {
            int index = i;
            yield return PropertyRow.Editable(table.Strings[i].Key, table.Strings[i].Value, value => Set(index, value));
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
    private void Set(int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A string cannot contain a NUL character; the table uses it as the terminator.");
        }
        FcolTable table = FcolTable.Parse(Entry.Payload);
        table.Strings[index] = (table.Strings[index].Key, value);
        Entry.Replace(table.Build());
    }
}
internal sealed class PlateNode : EntryNode
{
    public PlateNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Auto, $"{Plate.Parse(entry.Payload).Rects.Count} rectangles")
    {
    }
    protected override void Check(byte[] payload)
    {
        _ = Plate.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        Plate plate = Plate.Parse(Entry.Payload);
        yield return PropertyRow.Of("Atlas", $"{plate.Width} x {plate.Height}");
        yield return PropertyRow.Of("Rectangles", plate.Rects.Count);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
        foreach ((string name, int x, int y, int w, int h) in plate.Rects)
        {
            yield return PropertyRow.Of(name, $"{w}x{h} at ({x}, {y})");
        }
    }
}
