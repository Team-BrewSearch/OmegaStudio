using Kartz.Formats.Binary;

namespace Kartz.Formats;

/// <summary>
/// The authored per-track texture order, read from the game's resource databases.
///
/// <para>
/// Both games ship a record database alongside the archive triple: Super Star Kartz splits it
/// into <c>main.rdf</c> plus one <c>maintrack&lt;track&gt;.rdf</c> per track, Madagascar Kartz
/// puts every track in a single <c>main.rdf</c>. The body is a flat stream of 8-byte
/// <c>(u32 type, u32 value)</c> entries; a type of 1 means "value is an archive entry index",
/// and a level's textures appear as those, in the order the level author listed them. That
/// order is the order a TRK's 1-based <c>texture_index</c> counts through — the same order
/// <see cref="SskTextureSlotSolver"/> otherwise has to infer from geometry.
/// </para>
///
/// <para>
/// The references are not one unbroken run: a track can be mentioned again further down the
/// database, and reading only the longest contiguous run misses it — that is how WateringHole
/// lost <c>WH_river</c>, listed alone 676 bytes past the end of its run. So every reference is
/// collected, in file order, and the caller says which archive entries belong to the track.
/// </para>
/// </summary>
public sealed class KartzResourceIndex
{
    // Type tag marking a value as an archive entry index. Other tags carry inline data.
    private const uint ArchiveReference = 1;

    private readonly List<(string File, int[] Values)> _files;

    private KartzResourceIndex(List<(string File, int[] Values)> files)
    {
        _files = files;
    }

    /// <summary>Databases that held at least one archive reference.</summary>
    public int DatabaseCount => _files.Count;

    /// <summary>
    /// Reads every <c>.rdf</c> beside the archive. Returns null when the directory holds none,
    /// which is the signal to fall back to solving the order from geometry alone.
    /// </summary>
    /// <param name="archiveDirectory">The folder holding the GDA/GDF/GDT triple.</param>
    /// <param name="archiveEntryCount">Entry count, used to reject out-of-range references.</param>
    public static KartzResourceIndex? Load(string archiveDirectory, int archiveEntryCount)
    {
        if (string.IsNullOrEmpty(archiveDirectory)
            || archiveEntryCount <= 0
            || !Directory.Exists(archiveDirectory))
        {
            return null;
        }

        List<(string File, int[] Values)> files = [];
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

            int[] values = [.. CollectReferences(data, archiveEntryCount)];
            if (values.Length > 0)
            {
                files.Add((Path.GetFileName(path), values));
            }
        }

        return files.Count > 0 ? new KartzResourceIndex(files) : null;
    }

    /// <summary>
    /// Returns a track's textures in the order its database lists them, or null when no
    /// database mentions any of them.
    /// </summary>
    /// <param name="own">Archive entry indices of textures in the track's own directories.</param>
    public IReadOnlyList<int>? FindTextureOrder(IReadOnlySet<int> own)
    {
        ArgumentNullException.ThrowIfNull(own);

        // A track with its own maintrack<name>.rdf can still be mentioned once in main.rdf.
        // Reading files in name order would let that stray reference open the sequence and
        // shift every slot after it, so the database that describes this track most goes first.
        List<(string File, int[] Values)> ordered = [.. _files
            .OrderByDescending(f => f.Values.Count(own.Contains))
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)];

        List<int> sequence = [];
        HashSet<int> seen = [];

        foreach ((_, int[] values) in ordered)
        {
            foreach (int value in values)
            {
                if (own.Contains(value) && seen.Add(value))
                {
                    sequence.Add(value);
                }
            }
        }

        return sequence.Count > 0 ? sequence : null;
    }

    /// <summary>Every archive reference in one database body, in file order.</summary>
    private static List<int> CollectReferences(byte[] data, int archiveEntryCount)
    {
        // References are 8-byte aligned within a record but records are only 4-byte aligned as
        // a group, so step by 4 to find one and by 8 to stay inside a run of them.
        List<int> values = [];
        int i = 0;
        while (i + 8 <= data.Length)
        {
            if (!IsReference(data, i, archiveEntryCount))
            {
                i += 4;
                continue;
            }

            while (i + 8 <= data.Length && IsReference(data, i, archiveEntryCount))
            {
                values.Add((int)BinaryRead.U32(data, i + 4));
                i += 8;
            }
        }

        return values;
    }

    private static bool IsReference(byte[] data, int at, int archiveEntryCount)
    {
        return BinaryRead.U32(data, at) == ArchiveReference
            && BinaryRead.U32(data, at + 4) < (uint)archiveEntryCount;
    }
}
