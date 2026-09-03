using SMath = System.Math;
namespace Kartz.Formats;

public sealed class TrackTextureTable
{
    private readonly DwkArchive _archive;
    private readonly KartzResourceIndex? _resources;
    public TrackTextureTable(DwkArchive archive, KartzResourceIndex? resources)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
        _resources = resources;
    }
    public List<DwkEntry?> Resolve(DwkEntry trackEntry, SskTrack track)
    {
        ArgumentNullException.ThrowIfNull(trackEntry);
        ArgumentNullException.ThrowIfNull(track);
        IReadOnlyList<int[]>? slots = _resources?.TextureSlots(trackEntry.Index);
        int count = SMath.Max((slots?.Count ?? 1) - 1, track.ReferencedSlotCount);
        List<DwkEntry?> table = new(count);
        bool resolved = false;
        for (int slot = 1; slot <= count; slot++)
        {
            DwkEntry? found = slots is not null && slot < slots.Count ? Texture(slots[slot]) : null;
            resolved |= found is not null;
            table.Add(found);
        }
        if (resolved || Sibling(trackEntry) is not { } beside)
        {
            return table;
        }
        for (int slot = 0; slot < table.Count; slot++)
        {
            table[slot] = beside;
        }
        return table;
    }
    private DwkEntry? Sibling(DwkEntry model)
    {
        string path = model.NormalizedPath;
        int cut = path.LastIndexOf('/');
        if (cut < 0)
        {
            return null;
        }
        string folder = path[..(cut + 1)] + "Textures/";
        string stem = Path.GetFileNameWithoutExtension(path);
        DwkEntry? only = null;
        int candidates = 0;
        foreach (DwkEntry entry in _archive.Entries)
        {
            if (!entry.NormalizedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                || !SskTexture.IsTexture(entry.Span))
            {
                continue;
            }
            candidates++;
            only ??= entry;
            if (Path.GetFileNameWithoutExtension(entry.Name).Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return candidates == 1 ? only : null;
    }
    private DwkEntry? Texture(int[] ids)
    {
        foreach (int id in ids)
        {
            if (id < 0 || id >= _archive.Entries.Count)
            {
                continue;
            }
            DwkEntry entry = _archive.Entries[id];
            if (SskTexture.IsTexture(entry.Span))
            {
                return entry;
            }
        }
        return null;
    }
}
