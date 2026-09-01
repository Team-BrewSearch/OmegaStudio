using Kartz.Formats.Rdf;
namespace Kartz.Formats;

public sealed class KartzResourceIndex
{
    private readonly Dictionary<int, int[][]> _slotsByModel;
    private KartzResourceIndex(Dictionary<int, int[][]> slotsByModel, int databaseCount)
    {
        _slotsByModel = slotsByModel;
        DatabaseCount = databaseCount;
    }
    public int DatabaseCount { get; }
    public static KartzResourceIndex? Load(string archiveDirectory, int archiveEntryCount)
    {
        if (string.IsNullOrEmpty(archiveDirectory)
            || archiveEntryCount <= 0
            || !Directory.Exists(archiveDirectory))
        {
            return null;
        }
        Dictionary<int, int[][]> slots = [];
        int databases = 0;
        foreach (string path in Directory.EnumerateFiles(archiveDirectory, "*.rdf")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            if (RdfDocument.Load(data) is not { } document)
            {
                continue;
            }
            databases++;
            Collect(document.Root, archiveEntryCount, slots, []);
        }
        return databases > 0 ? new KartzResourceIndex(slots, databases) : null;
    }
    public IReadOnlyList<int[]>? TextureSlots(int modelEntryIndex)
    {
        return _slotsByModel.TryGetValue(modelEntryIndex, out int[][]? slots) ? slots : null;
    }
    private static void Collect(
        RdfRecord record,
        int archiveEntryCount,
        Dictionary<int, int[][]> slots,
        HashSet<RdfRecord> visited)
    {
        if (!visited.Add(record))
        {
            return;
        }
        Register(record, archiveEntryCount, slots);
        foreach (RdfProperty property in record.Properties)
        {
            if (property.Record is { } child)
            {
                Collect(child, archiveEntryCount, slots, visited);
            }
            if (property.Records is not { } children)
            {
                continue;
            }
            foreach (RdfRecord element in children)
            {
                Collect(element, archiveEntryCount, slots, visited);
            }
        }
    }
    private static void Register(RdfRecord record, int archiveEntryCount, Dictionary<int, int[][]> slots)
    {
        if (record.Find("model") is not { Ids: { Length: > 0 } ids }
            || SlotTable(record) is not { } table)
        {
            return;
        }
        foreach (int id in ids)
        {
            if (id >= 0 && id < archiveEntryCount)
            {
                slots[id] = table;
            }
        }
    }
    private static int[][]? SlotTable(RdfRecord record)
    {
        if (record.Find("textures") is not { } textures)
        {
            return null;
        }
        IReadOnlyList<RdfRecord>? entries = textures.Records
            ?? textures.Record?.Find("texture")?.Records;
        if (entries is null)
        {
            return null;
        }
        int[][] table = new int[entries.Count][];
        for (int i = 0; i < entries.Count; i++)
        {
            table[i] = entries[i].Find("name")?.Ids ?? [];
        }
        return table;
    }
}
