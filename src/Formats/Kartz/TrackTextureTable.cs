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
        string folder = path[..(cut + 1)];
        string stem = Path.GetFileNameWithoutExtension(path);
        DwkEntry? best = null;
        int bestScore = -1;
        bool tied = false;
        foreach (DwkEntry entry in _archive.Entries)
        {
            if (!entry.NormalizedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                || !SskTexture.IsTexture(entry.Span))
            {
                continue;
            }
            int score = Shared(stem, Path.GetFileNameWithoutExtension(entry.Name));
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }
        return tied ? null : best;
    }
    private static int Shared(string stem, string candidate)
    {
        int tail = 0;
        while (tail < stem.Length && tail < candidate.Length
            && char.ToLowerInvariant(stem[^(tail + 1)]) == char.ToLowerInvariant(candidate[^(tail + 1)]))
        {
            tail++;
        }
        int head = 0;
        while (head < stem.Length && head < candidate.Length
            && char.ToLowerInvariant(stem[head]) == char.ToLowerInvariant(candidate[head]))
        {
            head++;
        }
        return head + tail;
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
