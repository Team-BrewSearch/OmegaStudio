using Kartz.Formats.Binary;
namespace Kartz.Formats;

public sealed class KartzResourceIndex
{
    private const uint ArchiveReference = 1;
    private readonly List<(string File, int[] Values)> _files;
    private KartzResourceIndex(List<(string File, int[] Values)> files)
    {
        _files = files;
    }
    public int DatabaseCount => _files.Count;
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
    public IReadOnlyList<int>? FindTextureOrder(IReadOnlySet<int> own)
    {
        ArgumentNullException.ThrowIfNull(own);
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
    private static List<int> CollectReferences(byte[] data, int archiveEntryCount)
    {
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
