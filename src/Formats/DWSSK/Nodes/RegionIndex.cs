using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
using DWSSK.Formats.Wad;
using Omega.Plugins;

namespace DWSSK.Formats.Nodes;

internal sealed record RegionWad(string Name, Func<(Platform? Platform, IEnumerable<(int Type, string File, uint Hash)> Entries)> List, Func<string, byte[]> Read);

internal sealed class RegionIndex
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, RegionIndex> s_cache = new(StringComparer.Ordinal);

    private readonly string _root;
    private readonly Func<IEnumerable<RegionWad>> _wads;
    private readonly IHostServices _host;
    private readonly Lazy<Dictionary<uint, (RegionWad Wad, string File, Platform? Platform)>> _index;
    private readonly Dictionary<uint, Slot?> _found = [];

    private RegionIndex(string root, Func<IEnumerable<RegionWad>> wads, IHostServices host)
    {
        _root = root;
        _wads = wads;
        _host = host;
        _index = new(Build);
    }

    public static RegionIndex For(string root, bool extracted, IHostServices host)
    {
        string full = Path.GetFullPath(root);
        return Cached((extracted ? "x:" : "w:") + full, () => new RegionIndex(full, () => OnDisk(full, extracted), host));
    }

    public static RegionIndex ForRows(string root, Func<IEnumerable<(string Path, Func<byte[]> Bytes)>> rows, IHostServices host)
    {
        return new RegionIndex(root, () => rows()
            .OrderBy(r => Relative(root, r.Path), StringComparer.Ordinal)
            .Select(r => FromBytes(r.Path, r.Bytes, Paths.PlatformOf(r.Path))), host);
    }

    private static RegionIndex Cached(string key, Func<RegionIndex> create)
    {
        lock (s_lock)
        {
            if (!s_cache.TryGetValue(key, out RegionIndex? index))
            {
                s_cache[key] = index = create();
            }
            return index;
        }
    }

    private static string Relative(string root, string path)
    {
        string rel = path.Replace('\\', '/');
        string prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        return (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? rel[prefix.Length..] : rel).Replace('/', '\0');
    }

    private static IEnumerable<RegionWad> OnDisk(string root, bool extracted)
    {
        IEnumerable<string> found = extracted
            ? Directory.EnumerateFiles(root, "_manifest.json", SearchOption.AllDirectories).Select(Path.GetDirectoryName).OfType<string>()
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => WadFiles.Pattern().IsMatch(Path.GetFileName(f)));
        foreach (string wad in found.OrderBy(w => Relative(root, w), StringComparer.Ordinal))
        {
            yield return extracted
                ? new RegionWad(wad, () =>
                {
                    JObj man = J.O(PyJson.Loads(File.ReadAllBytes(Path.Combine(wad, "_manifest.json"))));
                    return (Platforms.Parse(J.S(man["platform"])), J.A(man["entries"]).Select(J.O).Select(m => (J.I(m["type"]), J.S(m["file"]), J.U(m["hash"]))));
                }, file => File.ReadAllBytes(Path.Combine(wad, file)))
                : FromBytes(wad, () => File.ReadAllBytes(wad), Paths.PlatformOf(wad));
        }
    }

    private static RegionWad FromBytes(string name, Func<byte[]> bytes, Platform? hint)
    {
        return new RegionWad(name, () =>
        {
            WadArchive archive = WadArchive.Read(bytes(), hint);
            return (archive.Platform, archive.Entries.Select(e => (e.Type, e.File, e.Hash)));
        }, file => WadArchive.Read(bytes(), hint).Entries.First(e => e.File == file && !e.IsAlias).Data);
    }

    private Dictionary<uint, (RegionWad Wad, string File, Platform? Platform)> Build()
    {
        Dictionary<uint, (RegionWad, string, Platform?)> index = [];
        int wads = 0;
        foreach (RegionWad wad in _wads())
        {
            try
            {
                (Platform? platform, IEnumerable<(int Type, string File, uint Hash)> entries) = wad.List();
                foreach ((int type, string file, uint hash) in entries)
                {
                    if (type != WadArchive.AliasType && file.EndsWith(".htx", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = index.TryAdd(hash, (wad, file, platform));
                    }
                }
                wads++;
            }
            catch (Exception ex) when (Failure.IsData(ex))
            {
                _host.Log($"{wad.Name}: skipped while indexing textures: {ex.Message}");
            }
        }
        _host.Log($"Indexed {index.Count} textures in {wads} containers under {(_root.Length == 0 ? "the disc's game folder" : _root)} for textures a model's own WAD and global.wad lack.");
        return index;
    }

    public Slot? Find(uint hash)
    {
        lock (_found)
        {
            if (_found.TryGetValue(hash, out Slot? cached))
            {
                return cached;
            }
            Slot? slot = null;
            if (_index.Value.TryGetValue(hash, out (RegionWad Wad, string File, Platform? Platform) at))
            {
                byte[] data = at.Wad.Read(at.File);
                LooseSlot loose = new($"{Path.GetFileName(at.Wad.Name)}/{at.File}", at.Platform, data.Length, () => data, null);
                _ = new LooseGroup(_host, [loose]);
                slot = loose;
            }
            _found[hash] = slot;
            return slot;
        }
    }

    public static string? WadRoot(string fullPath)
    {
        Match m = WadFiles.Pattern().Match(fullPath);
        if (!m.Success)
        {
            return null;
        }
        string basePath = m.Groups[1].Value;
        return WadFiles.IsGlobal(basePath) ? Path.GetDirectoryName(basePath)
            : GlobalWads.FindNear(fullPath) is { } global ? Path.GetDirectoryName(global) : null;
    }
}
