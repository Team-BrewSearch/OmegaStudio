using System.Text.RegularExpressions;
using DWSSK.Formats;
using DWSSK.Formats.Audio;
using DWSSK.Formats.Core;
using DWSSK.Formats.Data;
using DWSSK.Formats.Nodes;
using DWSSK.Formats.Wad;
using Omega.Plugins;

[assembly: OmegaPlugin(typeof(DwsskPlugin))]

namespace DWSSK.Formats;

public sealed class DwsskPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "dwssk",
        DisplayName = "DreamWorks Super Star Kartz",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "WAD containers, textures, models, animations, level data, shaders, Wwise audio and Bink video "
            + "from the PS3, Xbox 360, Wii and 3DS releases, on disk or inside a disc image.",
    };

    public IEnumerable<IArchiveFormat> Archives { get; } =
    [
        new WadFormat(),
        new LooseFormat("dwssk.bnk", "Wwise sound bank (.bnk)", new FileFilter("Wwise sound bank", "*.bnk"), [".bnk"], b => Bnk.IsBank(b)),
        new LooseFormat("dwssk.wem", "Wwise audio (.wem)", new FileFilter("Wwise audio", "*.wem"), [".wem"], b => Wem.IsWem(b)),
        new LooseFormat("dwssk.swf", "Scaleform movie (.swf)", new FileFilter("Scaleform movie", "*.swf"), [".swf"], b => Swf.IsSwf(b)),
        new LooseFormat("dwssk.nog", "Level name table (.nog)", new FileFilter("Level name table", "*.nog"), [".nog"], b => Nog.IsNog(b)),
        new LooseFormat("dwssk.bik", "Bink video (.bik)", new FileFilter("Bink video", "*.bik"), [".bik"], b => Video.BinkFile.IsBink(b)),
        new LooseFormat("dwssk.extracted", "DWSSK file extracted from a WAD", new FileFilter("DWSSK file", [.. LooseFormat.Extracted.Select(e => "*" + e)]), LooseFormat.Extracted, null),
    ];

    public IEnumerable<IContainerRecognizer> Containers { get; } = [new DwsskContainerRecognizer()];
}

internal static partial class WadFiles
{
    [GeneratedRegex(@"^(.*\.wad)(\.mem1|\.vram)?$", RegexOptions.IgnoreCase)]
    public static partial Regex Pattern();

    public static readonly string[] Suffixes = [string.Empty, ".mem1", ".vram"];

    public static bool IsGlobal(string basePath)
    {
        return Path.GetFileName(basePath).Equals("global.wad", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WadFormat : IArchiveFormat
{
    public string Id => "dwssk.wad";
    public string DisplayName => "DWSSK WAD (.wad, .wad.mem1, .wad.vram)";
    public FileFilter Filter { get; } = new("DWSSK WAD", "*.wad", "*.wad.mem1", "*.wad.vram");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        return WadFiles.Pattern().IsMatch(Path.GetFileName(path)) && WadArchive.IsWad(head);
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string full = Path.GetFullPath(path);
        string basePath = WadFiles.Pattern().Match(full).Groups[1].Value;
        Platform? hint = Paths.PlatformOf(full);
        List<(string Path, WadSession Session)> parts = [];
        foreach (string suffix in WadFiles.Suffixes)
        {
            string part = basePath + suffix;
            if (File.Exists(part) && (part == full || WadArchive.IsWad(Head(part))))
            {
                WadArchive archive = WadArchive.Read(File.ReadAllBytes(part), hint);
                parts.Add((part, new WadSession(archive, Path.GetFileName(part), true)));
            }
        }
        Func<WadGroup?>? global = null;
        if (!WadFiles.IsGlobal(basePath) && GlobalWads.FindNear(full) is { } near)
        {
            global = GlobalWads.FromFile(near, host);
        }
        WadGroup group = new(host, [.. parts.Select(p => p.Session)], global)
        {
            Region = RegionIndex.WadRoot(full) is { } root ? RegionIndex.For(root, false, host) : null,
        };
        host.Log($"{Path.GetFileName(basePath)}: {string.Join(", ", parts.Select(p => $"{p.Session.Name} ({p.Session.Archive.Entries.Count} entries)"))}, "
            + $"{group.Platform.Label()}{(global is null ? string.Empty : ", textures also looked up in global.wad")}.");
        return new WadArchiveHandle(Path.GetFileName(basePath), parts, group);
    }

    private static byte[] Head(string path)
    {
        using FileStream f = File.OpenRead(path);
        byte[] head = new byte[(int)Math.Min(16, f.Length)];
        f.ReadExactly(head);
        return head;
    }
}

internal static class WadRoots
{
    public static IAssetNode Build(string name, WadGroup group)
    {
        IReadOnlyList<WadSession> sessions = group.Sessions;
        return sessions.Count == 1
            ? new WadNode(sessions[0], sessions[0].Name)
            : new GroupNode(name, string.Empty, $"{sessions.Count} parts, {group.Platform.Label()}",
                () => sessions.Select(s => (IAssetNode)new WadNode(s, s.Name)));
    }
}

internal sealed class WadArchiveHandle : IArchive
{
    private readonly List<(string Path, WadSession Session)> _parts;

    public WadArchiveHandle(string name, List<(string Path, WadSession Session)> parts, WadGroup group)
    {
        _parts = parts;
        DisplayName = name;
        Root = WadRoots.Build(name, group);
    }

    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _parts.Any(p => p.Session.Archive.IsDirty);
    public IReadOnlyList<string> BackupPaths => [.. _parts.Select(p => p.Path)];

    public void Save()
    {
        List<(string Path, WadSession Session)> dirty = [.. _parts.Where(p => p.Session.Archive.IsDirty)];
        List<(string Path, byte[] Rebuilt)> files = [.. dirty.Select(p => (p.Path, p.Session.Archive.Build()))];
        WriteGuard.Commit($"the WAD {DisplayName}", files, temporaries =>
        {
            for (int i = 0; i < temporaries.Count; i++)
            {
                WadArchive archive = dirty[i].Session.Archive;
                WadArchive back = WadArchive.Read(File.ReadAllBytes(temporaries[i]), archive.Platform);
                if (back.Entries.Count != archive.Entries.Count
                    || !back.Entries.Zip(archive.Entries).All(x => x.First.File == x.Second.File && x.First.Data.AsSpan().SequenceEqual(x.Second.Data)))
                {
                    return false;
                }
            }
            return true;
        });
        foreach ((_, WadSession session) in dirty)
        {
            session.Archive.MarkSaved();
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class LooseFormat(string id, string name, FileFilter filter, string[] extensions, Func<byte[], bool>? magic) : IArchiveFormat
{
    public static readonly string[] Extracted =
    [
        ".htx", ".gdw", ".gds", ".gdi", ".gdt", ".piece", ".animpack", ".p2b", ".lcl", ".fkd", ".cam", ".csd",
        ".nav", ".col", ".vs_bin", ".ps_bin", ".bog", ".arc", ".tpl",
    ];

    public string Id => id;
    public string DisplayName => name;
    public FileFilter Filter => filter;

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!extensions.Contains(ext))
        {
            return false;
        }
        return magic is not null ? magic(head.ToArray()) : Paths.PlatformOf(Path.GetFullPath(path)) is not null || Extract.Manifest(path) is not null;
    }

    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string full = Path.GetFullPath(path);
        if (magic is null)
        {
            return Extract.Open(full, host);
        }
        byte[] data = File.ReadAllBytes(full);
        LooseSlot slot = new(Path.GetFileName(full), Paths.PlatformOf(full), data.Length, () => data, _ => { });
        _ = new LooseGroup(host, [slot]);
        return new LooseArchive(full, slot);
    }
}

internal sealed class LooseArchive(string path, LooseSlot slot) : IArchive
{
    public string DisplayName { get; } = Path.GetFileName(path);
    public IAssetNode Root { get; } = NodeFactory.Create(slot, Path.GetFileName(path), Path.GetFileName(path));
    public bool IsDirty => slot.IsDirty;
    public IReadOnlyList<string> BackupPaths => [path];

    public void Save()
    {
        List<(string Path, byte[] Rebuilt)> files = [(path, slot.Data)];
        WriteGuard.Commit(DisplayName, files, temporaries =>
        {
            byte[] back = File.ReadAllBytes(temporaries[0]);
            (Root as SlotNode)?.Validate(back);
            return back.AsSpan().SequenceEqual(slot.Data);
        });
        slot.MarkSaved();
    }

    public void Dispose()
    {
    }
}

internal static class Extract
{
    public static string? Manifest(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        for (int depth = 0; depth < 12 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir, "_manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static IArchive Open(string full, IHostServices host)
    {
        string? manifest = Manifest(full);
        if (manifest is null || PyJson.Loads(File.ReadAllBytes(manifest)) is not JObj man)
        {
            byte[] data = File.ReadAllBytes(full);
            LooseSlot alone = new(Path.GetFileName(full), Paths.PlatformOf(full), data.Length, () => data, _ => { });
            _ = new LooseGroup(host, [alone]);
            return new LooseArchive(full, alone);
        }
        string dir = Path.GetDirectoryName(manifest)!;
        string self = Path.GetRelativePath(dir, full).Replace(Path.DirectorySeparatorChar, '/');
        List<WadSession> sessions = [Session(dir, man, true)];
        Match m = WadFiles.Pattern().Match(dir);
        if (m.Success)
        {
            foreach (string suffix in WadFiles.Suffixes)
            {
                string other = m.Groups[1].Value + suffix;
                string otherManifest = Path.Combine(other, "_manifest.json");
                if (other != dir && File.Exists(otherManifest) && PyJson.Loads(File.ReadAllBytes(otherManifest)) is JObj om)
                {
                    sessions.Add(Session(other, om, false));
                }
            }
        }
        string? globalDir = m.Success && !WadFiles.IsGlobal(m.Groups[1].Value) ? GlobalFolder(dir) : null;
        Func<WadGroup?>? global = globalDir is null ? null : () =>
            new WadGroup(host, [Session(globalDir, J.O(PyJson.Loads(File.ReadAllBytes(Path.Combine(globalDir, "_manifest.json")))), false)], null);
        WadGroup group = new(host, sessions, global)
        {
            Region = globalDir is null ? null : RegionIndex.For(Path.GetDirectoryName(globalDir)!, true, host),
        };
        WadSlot slot = sessions[0].Slots.FirstOrDefault(s => s.Entry.File == self)
            ?? throw new InvalidDataException($"{self} is not listed in {manifest}");
        return new ExtractedArchive(full, slot, group);
    }

    private static WadSession Session(string dir, JObj man, bool writable)
    {
        WadArchive archive = WadArchive.FromManifest(man, f => File.Exists(Path.Combine(dir, f)) ? File.ReadAllBytes(Path.Combine(dir, f)) : []);
        return new WadSession(archive, Path.GetFileName(dir), writable);
    }

    private static string? GlobalFolder(string wadDir)
    {
        string? dir = Path.GetDirectoryName(wadDir);
        for (int depth = 0; depth < 4 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir, "global.wad");
            if (File.Exists(Path.Combine(candidate, "_manifest.json")) && candidate != wadDir)
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

internal sealed class ExtractedArchive(string path, WadSlot slot, WadGroup group) : IArchive
{
    public string DisplayName { get; } = Path.GetFileName(path);
    public IAssetNode Root { get; } = NodeFactory.Create(slot, Path.GetFileName(path), slot.File);
    public bool IsDirty => slot.Session.Archive.IsDirty;
    public IReadOnlyList<string> BackupPaths => [path];

    public WadGroup Group { get; } = group;

    public void Save()
    {
        List<(string Path, byte[] Rebuilt)> files = [(path, slot.Data)];
        WriteGuard.Commit(DisplayName, files, temporaries =>
        {
            byte[] back = File.ReadAllBytes(temporaries[0]);
            (Root as SlotNode)?.Validate(back);
            return back.AsSpan().SequenceEqual(slot.Data);
        });
        slot.Session.Archive.MarkSaved();
    }

    public void Dispose()
    {
    }
}

public sealed class DwsskContainerRecognizer : IContainerRecognizer
{
    public string Id => "dwssk.container";

    private static readonly string[] s_loose = [".bnk", ".wem", ".swf", ".nog", ".bik"];

    public IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(host);
        if (children.Any(c => c is SlotNode or WadNode))
        {
            return null;
        }
        Dictionary<string, List<(string Suffix, IAssetNode Row)>> wads = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAssetNode child in children)
        {
            Match m = WadFiles.Pattern().Match(child.Name);
            if (m.Success && Row(child) is { } row && WadArchive.IsWad(row.Head))
            {
                string key = m.Groups[1].Value;
                if (!wads.TryGetValue(key, out List<(string, IAssetNode)>? list))
                {
                    wads[key] = list = [];
                }
                list.Add((m.Groups[2].Value.ToLowerInvariant(), child));
            }
        }
        List<IAssetNode> loose = [.. children.Where(c => s_loose.Contains(Path.GetExtension(c.Name).ToLowerInvariant()) && Row(c) is not null)];
        if (wads.Count == 0 && loose.Count == 0)
        {
            return null;
        }
        Dictionary<IAssetNode, IAssetNode?> replace = [];
        foreach ((string key, List<(string Suffix, IAssetNode Row)> rows) in wads)
        {
            List<(string Suffix, IAssetNode Row)> ordered = [.. rows.OrderBy(r => Array.IndexOf(WadFiles.Suffixes, r.Suffix))];
            Lazy<WadGroup?> group = new(() => OpenWad(key, ordered, host));
            if (WadFiles.IsGlobal(key))
            {
                string root = Folder(ordered[0].Row.PathText);
                GlobalWads.Offer(() => group.Value, RegionIndex.ForRows(root, () => WadRows(children), host));
            }
            foreach ((_, IAssetNode row) in rows)
            {
                replace[row] = null;
            }
            long size = rows.Sum(r => (long)(Row(r.Row)?.Size ?? 0));
            IAssetNode first = ordered[0].Row;
            replace[first] = new LazyNode(key, first.PathText, $"DWSSK WAD, {rows.Count} part{(rows.Count == 1 ? "" : "s")}, {Sizes.Describe(size)}",
                () => group.Value is { } g ? WadRoots.Build(key, g) : new GroupNode(key, first.PathText, "did not open", () => rows.Select(r => r.Row)));
        }
        List<LooseSlot> slots = [];
        Dictionary<IAssetNode, LooseSlot> bySlot = [];
        foreach (IAssetNode child in loose)
        {
            IBinaryAsset row = Row(child)!;
            LooseSlot slot = new(child.Name, null, row.Size, () => Copy(row), row.CanReplaceBytes ? row.ReplaceBytes : null);
            slots.Add(slot);
            bySlot[child] = slot;
        }
        if (slots.Count > 0)
        {
            _ = new LooseGroup(host, slots);
        }
        List<IAssetNode> output = [];
        foreach (IAssetNode child in children)
        {
            if (replace.TryGetValue(child, out IAssetNode? node))
            {
                if (node is not null)
                {
                    output.Add(node);
                }
            }
            else if (bySlot.TryGetValue(child, out LooseSlot? slot))
            {
                output.Add(NodeFactory.Create(slot, child.Name, child.PathText));
            }
            else
            {
                output.Add(child);
            }
        }
        return output;
    }

    private static IBinaryAsset? Row(IAssetNode node)
    {
        return (node is RecognizedAsset r ? r.Inner : node) as IBinaryAsset;
    }

    private static byte[] Copy(IBinaryAsset row)
    {
        byte[] bytes = row.Data.ToArray();
        (row as StreamingAssetNode)?.ReleasePayload();
        return bytes;
    }

    private static string Folder(string path)
    {
        int cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? string.Empty : path[..cut];
    }

    private static IEnumerable<(string Path, Func<byte[]> Bytes)> WadRows(IEnumerable<IAssetNode> nodes)
    {
        foreach (IAssetNode node in nodes)
        {
            if (Row(node) is { } row)
            {
                if (WadFiles.Pattern().IsMatch(node.Name) && WadArchive.IsWad(row.Head))
                {
                    yield return (node.PathText, () => Copy(row));
                }
            }
            else if (node.HasChildren && node is not SlotNode and not WadNode and not LazyNode)
            {
                foreach ((string Path, Func<byte[]> Bytes) inner in WadRows(node.GetChildren()))
                {
                    yield return inner;
                }
            }
        }
    }

    private static WadGroup? OpenWad(string key, List<(string Suffix, IAssetNode Row)> rows, IHostServices host)
    {
        try
        {
            List<WadSession> sessions = [];
            foreach ((string suffix, IAssetNode child) in rows)
            {
                IBinaryAsset row = Row(child)!;
                WadArchive archive = WadArchive.Read(row.Data, Paths.PlatformOf(child.PathText));
                bool writable = row.CanReplaceBytes;
                if (writable)
                {
                    archive.Changed += () => row.ReplaceBytes(archive.Build());
                }
                if (row is StreamingAssetNode streaming)
                {
                    streaming.ReleasePayload();
                }
                sessions.Add(new WadSession(archive, key + suffix, writable));
            }
            WadGroup group = new(host, sessions, WadFiles.IsGlobal(key) ? null : GlobalWads.Latest)
            {
                Region = GlobalWads.LatestRegion,
            };
            host.Log($"{key}: {string.Join(", ", sessions.Select(s => $"{s.Name} ({s.Archive.Entries.Count} entries)"))}, {group.Platform.Label()}"
                + (sessions.All(s => s.Writable) ? "." : ". Read-only: the rows it is made of cannot be written back."));
            return group;
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            host.Log($"{key} looks like a DWSSK WAD but did not open: {ex.Message}");
            return null;
        }
    }
}

internal sealed class LazyNode(string name, string path, string detail, Func<IAssetNode> open)
    : AssetNode(name, path, AssetKinds.Container, detail), IDescribable, ICommandSource
{
    private readonly Lazy<IAssetNode> _inner = new(open);

    public override bool HasChildren => true;

    public override IEnumerable<IAssetNode> GetChildren()
    {
        return _inner.Value.GetChildren();
    }

    public IEnumerable<PropertyRow> Describe()
    {
        return _inner.Value is IDescribable d ? d.Describe() : [];
    }

    public IEnumerable<AssetCommand> GetCommands()
    {
        return _inner.Value is ICommandSource c ? c.GetCommands() : [];
    }
}
