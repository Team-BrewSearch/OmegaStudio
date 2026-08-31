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
        string trackDir = TrackDirectory(trackEntry);
        (List<DwkEntry> upper, List<DwkEntry> lower) = CollectGroups(trackDir);
        List<DwkEntry> own = [.. upper, .. lower];
        List<DwkEntry> all = [.. own, .. CollectScenery(trackDir)];
        (List<DwkEntry> ordered, List<DwkEntry> filler) = SplitByRun(own, all);
        if (ordered.Count > 0)
        {
            int slots = SMath.Max(ordered.Count + filler.Count, track.ReferencedSlotCount);
            List<DwkEntry?>? table = SskTextureSlotSolver.SolveOrdered(
                ordered, filler, track.MeasureSlotUsage(slots), ResolvePins(trackDir, all, slots));
            if (table is not null)
            {
                return table;
            }
        }
        SskTrack.SlotUsage[] usage = track.MeasureSlotUsage(upper.Count + lower.Count);
        return [.. SskTextureSlotSolver.Solve(upper, lower, usage)];
    }
    private static Dictionary<int, DwkEntry>? ResolvePins(
        string trackDirNorm, List<DwkEntry> candidates, int slots)
    {
        int lastSlash = trackDirNorm.LastIndexOf('/');
        string trackName = lastSlash >= 0 ? trackDirNorm[(lastSlash + 1)..] : trackDirNorm;
        if (TrackTexturePins.For(trackName) is not { } pins)
        {
            return null;
        }
        Dictionary<string, DwkEntry> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (DwkEntry entry in candidates)
        {
            _ = byName.TryAdd(entry.Name, entry);
        }
        if (!byName.ContainsKey(pins.Witness))
        {
            return null;
        }
        Dictionary<int, DwkEntry> resolved = [];
        foreach ((int slot, string name) in pins.Slots)
        {
            if (slot >= 1 && slot <= slots && byName.TryGetValue(name, out DwkEntry? entry))
            {
                resolved[slot - 1] = entry;
            }
        }
        return resolved.Count > 0 ? resolved : null;
    }
    private (List<DwkEntry> Ordered, List<DwkEntry> Filler) SplitByRun(
        List<DwkEntry> own, List<DwkEntry> all)
    {
        IReadOnlyList<int>? order = _resources?.FindTextureOrder(own.Select(e => e.Index).ToHashSet());
        if (order is null)
        {
            return ([], []);
        }
        Dictionary<int, DwkEntry> remaining = all.ToDictionary(e => e.Index);
        List<DwkEntry> ordered = new(order.Count);
        foreach (int index in order)
        {
            if (remaining.Remove(index, out DwkEntry? found))
            {
                ordered.Add(found);
            }
        }
        return (ordered, [.. all.Where(e => remaining.ContainsKey(e.Index))]);
    }
    private List<DwkEntry> CollectScenery(string trackDirNorm)
    {
        int lastSlash = trackDirNorm.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return [];
        }
        string parent = trackDirNorm[..(lastSlash + 1)];
        string name = trackDirNorm[(lastSlash + 1)..];
        string baseName = name.EndsWith("_Reverse", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_Reverse".Length]
            : name;
        List<DwkEntry> found = [];
        HashSet<string> bundles = [];
        foreach (DwkEntry entry in _archive.Entries)
        {
            string norm = entry.NormalizedPath;
            if (!norm.StartsWith(parent, StringComparison.Ordinal))
            {
                continue;
            }
            int slash = norm.IndexOf('/', parent.Length);
            if (slash < 0)
            {
                continue;
            }
            string dir = norm[parent.Length..slash];
            if (!dir.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                || dir.Equals(name, StringComparison.Ordinal)
                || dir.Equals(baseName, StringComparison.Ordinal))
            {
                continue;
            }
            _ = bundles.Add(dir);
        }
        foreach (string bundle in bundles.OrderBy(b => b, StringComparer.Ordinal))
        {
            found.AddRange(CollectTexGroup(parent + bundle, "Textures"));
            found.AddRange(CollectTexGroup(parent + bundle, "textures"));
        }
        return found;
    }
    private (List<DwkEntry> Upper, List<DwkEntry> Lower) CollectGroups(string trackDir)
    {
        return (CollectTexGroup(trackDir, "Textures"), CollectTexGroup(trackDir, "textures"));
    }
    private List<DwkEntry> CollectTexGroup(string trackDirNorm, string texSubdir)
    {
        string prefix = trackDirNorm + "/" + texSubdir + "/";
        HashSet<int> seen = [];
        return _archive.Entries
            .Where(e =>
            {
                return e.NormalizedPath.StartsWith(prefix, StringComparison.Ordinal) && SskTexture.TryReadDimensions(e.Span, out _, out _)
                    && e.GdaOffset >= 0
                    && seen.Add(e.GdaOffset);
            })
            .OrderBy(e => e.Index)
            .ToList();
    }
    private static string TrackDirectory(DwkEntry entry)
    {
        string norm = entry.NormalizedPath;
        int lastSlash = norm.LastIndexOf('/');
        return lastSlash >= 0 ? norm[..lastSlash] : string.Empty;
    }
}
