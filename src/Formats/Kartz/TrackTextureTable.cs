using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// Resolves the texture table a TRK's chunk headers index into.
///
/// <para>
/// A track's textures live in two sibling archive directories — <c>Textures/</c> and
/// <c>textures/</c> — and the runtime table is built as the level's resources load. That order
/// is authored rather than derivable from the archive, and <see cref="KartzResourceIndex"/>
/// reads it out of the <c>.rdf</c> databases shipped beside the triple: one
/// <c>maintrack&lt;track&gt;.rdf</c> per track in Super Star Kartz, a single <c>main.rdf</c>
/// covering every track in Madagascar Kartz.
/// </para>
///
/// <para>
/// The run is not the whole table. A level also loads textures for its scenery, and those take
/// slots the run says nothing about — which is why a track can index a slot beyond anything the
/// run names, and why Madagascar's ShippingFreighter draws slot 34 while owning 33 textures.
/// Which slots they take is left to <see cref="SskTextureSlotSolver.SolveOrdered"/>; for most
/// tracks the run is exactly as long as the count of slots the track draws, which leaves it no
/// freedom at all.
/// </para>
///
/// <para>
/// With no <c>.rdf</c> beside the archive there is no run at all, and the order falls back to
/// <see cref="SskTextureSlotSolver.Solve"/>, which infers it from the geometry alone.
/// </para>
/// </summary>
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

    /// <summary>
    /// Returns the table for one track: slot <c>i</c> is the texture a chunk header's
    /// <c>texture_index</c> of <c>i + 1</c> refers to, with nulls for slots no texture reaches.
    /// </summary>
    /// <param name="trackEntry">The archive entry the track was parsed from.</param>
    /// <param name="track">That track, parsed but not yet textured.</param>
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
            // The table has to cover every slot the geometry indexes even when the track owns
            // fewer textures than that — Madagascar's ShippingFreighter draws slot 34 with 33.
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

    /// <summary>
    /// Resolves the confirmed slots for this track into the entries they name, dropping any the
    /// track does not actually own.
    /// </summary>
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

        // The same track name exists in both games; without its witness texture this is a
        // different level that happens to share the name, and the pins do not describe it.
        if (!byName.ContainsKey(pins.Witness))
        {
            return null;
        }

        Dictionary<int, DwkEntry> resolved = [];
        foreach ((int slot, string name) in pins.Slots)
        {
            // Slots are recorded 1-based, matching a chunk header texture_index.
            if (slot >= 1 && slot <= slots && byName.TryGetValue(name, out DwkEntry? entry))
            {
                resolved[slot - 1] = entry;
            }
        }

        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>
    /// Splits a level's textures into the authored sequence and everything it omits.
    /// </summary>
    /// <param name="own">
    /// The track directory's own textures. Only these are matched against the database: the
    /// scenery bundle is listed elsewhere in the file, and for a reversed track that sharing
    /// the forward track's bundle it is listed *earlier*, which would put water at slot 1.
    /// </param>
    /// <param name="all">Those plus the scenery bundle - the full candidate pool.</param>
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

    /// <summary>
    /// The level's scenery bundle: the sibling track directory spelled differently only in
    /// case, which holds the <c>.zon</c> models and their textures.
    /// </summary>
    /// <remarks>
    /// <c>models/tracks/sharkbeach/</c> beside <c>models/tracks/SharkBeach/</c>, and likewise
    /// for foosarocks and volcano. Those textures do take slots, so they are candidates — but
    /// they sit outside the track directory the authored sequence is matched against, and a
    /// reversed track shares the forward track's bundle rather than owning one.
    /// </remarks>
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

            // Case-insensitively the same track, spelled differently: a separate bundle rather
            // than an alias. An exact match for either the track's own name or the base name is
            // a real track directory - for ShippingFreighter_Reverse the base name spells
            // ShippingFreighter, whose textures are that other track's, not this one's scenery.
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
        // Case-sensitive across the whole prefix. Textures/ and textures/ are distinct groups,
        // and a differently-cased *track* directory is a different bundle rather than an alias:
        // models/tracks/sharkbeach/ holds water.zon, water2.zon and their two textures, which
        // are not part of the models/tracks/SharkBeach/ slot table.
        string prefix = trackDirNorm + "/" + texSubdir + "/";

        HashSet<int> seen = [];
        return _archive.Entries
            .Where(e =>
            {
                if (!e.NormalizedPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                // Check content before marking the GDA offset as seen, so a non-texture entry
                // with the same offset does not block the real texture entry.
                return SskTexture.TryReadDimensions(e.Span, out _, out _)
                    && e.GdaOffset >= 0
                    && seen.Add(e.GdaOffset);
            })
            .OrderBy(e => e.Index)
            .ToList();
    }

    private static string TrackDirectory(DwkEntry entry)
    {
        // Derive the prefix with string ops — Path.GetDirectoryName re-introduces backslashes
        // on Windows, producing mixed separators.
        string norm = entry.NormalizedPath;
        int lastSlash = norm.LastIndexOf('/');
        return lastSlash >= 0 ? norm[..lastSlash] : string.Empty;
    }
}
