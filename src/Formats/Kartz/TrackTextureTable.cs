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
        if (slots is null)
        {
            return [];
        }
        int count = SMath.Max(slots.Count - 1, track.ReferencedSlotCount);
        List<DwkEntry?> table = new(count);
        for (int slot = 1; slot <= count; slot++)
        {
            table.Add(slot < slots.Count ? Texture(slots[slot]) : null);
        }
        return table;
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
