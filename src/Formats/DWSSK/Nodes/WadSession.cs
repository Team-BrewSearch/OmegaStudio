using DWSSK.Formats.Core;
using DWSSK.Formats.Wad;
using Omega.Plugins;
namespace DWSSK.Formats.Nodes;
internal sealed class WadSession
{
    private List<WadSlot>? _slots;
    private List<WadEntry>? _slotsFor;
    public WadSession(WadArchive archive, string name, bool writable)
    {
        Archive = archive;
        Name = name;
        Writable = writable;
    }
    public WadArchive Archive { get; }
    public string Name { get; }
    public bool Writable { get; }
    public WadGroup Group { get; set; } = null!;
    public string Suffix => Name.EndsWith(".mem1", StringComparison.OrdinalIgnoreCase) ? ".mem1"
        : Name.EndsWith(".vram", StringComparison.OrdinalIgnoreCase) ? ".vram" : string.Empty;
    public IReadOnlyList<WadSlot> Slots
    {
        get
        {
            if (_slots is null || !ReferenceEquals(_slotsFor, Archive.Entries) || _slots.Count != Archive.Entries.Count)
            {
                Dictionary<WadEntry, WadSlot> old = _slots?.ToDictionary(s => s.Entry) ?? [];
                _slots = [.. Archive.Entries.Select(e => old.TryGetValue(e, out WadSlot? s) ? s : new WadSlot(e, this))];
                _slotsFor = Archive.Entries;
            }
            return _slots;
        }
    }
}
internal sealed class WadSlot(WadEntry entry, WadSession session) : Slot
{
    public WadEntry Entry { get; } = entry;
    public WadSession Session { get; } = session;
    public override string File => Entry.File;
    public override Platform Platform => Session.Archive.Platform;
    public override byte[] Data => Entry.IsAlias ? Alias() : Entry.Data;
    public override int Revision => Entry.Revision;
    public override bool CanWrite => Session.Writable && !Entry.IsAlias;
    public override uint? Hash => Entry.Hash;
    public override SlotGroup Group => Session.Group;
    private byte[] Alias()
    {
        if (Entry.AliasEntry >= 0 && Entry.AliasEntry < Session.Archive.Entries.Count)
        {
            WadEntry target = Session.Archive.Entries[Entry.AliasEntry];
            return Entry.AliasOffset is int legacy ? Bin.Slice(target.Data, legacy, target.Data.Length).ToArray() : target.Data;
        }
        return [];
    }
    public override void Replace(byte[] data)
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException(Entry.IsAlias
                ? $"{Entry.File} is an alias of another entry; replace the entry it points at"
                : $"{Entry.File} sits in a container that cannot be written back");
        }
        Session.Archive.Replace(Entry, data);
    }
}
internal sealed class WadGroup : SlotGroup
{
    private readonly Func<WadGroup?>? _global;
    private WadGroup? _globalGroup;
    private bool _globalLoaded;
    public WadGroup(IHostServices host, IReadOnlyList<WadSession> sessions, Func<WadGroup?>? global)
        : base(host)
    {
        Sessions = [.. sessions.OrderBy(s => s.Suffix switch
        {
            "" => 0,
            ".mem1" => 1,
            _ => 2,
        })];
        _global = global;
        foreach (WadSession s in Sessions)
        {
            s.Group = this;
        }
    }
    public IReadOnlyList<WadSession> Sessions { get; }
    public override Platform Platform => Sessions[0].Archive.Platform;
    public override int Revision => Sessions.Sum(s => s.Archive.Revision);
    public override SlotGroup? Global
    {
        get
        {
            if (!_globalLoaded)
            {
                try
                {
                    _globalGroup = _global?.Invoke();
                    _globalLoaded = _globalGroup is not null;
                }
                catch (Exception ex) when (Failure.IsData(ex))
                {
                    _globalLoaded = true;
                    Host.Log($"global.wad could not be read for texture lookups: {ex.Message}");
                }
                if (ReferenceEquals(_globalGroup, this) || _globalGroup?.Platform != Platform)
                {
                    _globalGroup = null;
                }
            }
            return _globalGroup;
        }
    }
    public override IEnumerable<Slot> All()
    {
        return Sessions.SelectMany(s => s.Slots);
    }
    public override IEnumerable<Slot> Parts(Slot from)
    {
        WadSession? own = (from as WadSlot)?.Session;
        IEnumerable<WadSession> order = own is null ? Sessions : [own, .. Sessions.Where(s => !ReferenceEquals(s, own))];
        return order.SelectMany(s => s.Slots);
    }
    public override IEnumerable<IEnumerable<Slot>> Containers()
    {
        return Sessions.Select(s => s.Slots);
    }
}
internal static class GlobalWads
{
    private static readonly Lock s_lock = new();
    private static Func<WadGroup?>? s_latest;
    private static RegionIndex? s_region;
    public static void Offer(Func<WadGroup?> provider, RegionIndex? region)
    {
        Lazy<WadGroup?> once = new(provider);
        lock (s_lock)
        {
            s_latest = () => once.Value;
            s_region = region;
        }
    }
    public static RegionIndex? LatestRegion
    {
        get
        {
            lock (s_lock)
            {
                return s_region;
            }
        }
    }
    public static WadGroup? Latest()
    {
        Func<WadGroup?>? f;
        lock (s_lock)
        {
            f = s_latest;
        }
        return f?.Invoke();
    }
    public static string? FindNear(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        for (int depth = 0; depth < 4 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir, "global.wad");
            if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(path), StringComparison.Ordinal))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
    public static Func<WadGroup?> FromFile(string path, IHostServices host)
    {
        return () =>
        {
            List<WadSession> parts = [];
            foreach (string suffix in WadFiles.Suffixes)
            {
                if (File.Exists(path + suffix))
                {
                    WadArchive archive = WadArchive.Read(File.ReadAllBytes(path + suffix), Paths.PlatformOf(path));
                    parts.Add(new WadSession(archive, Path.GetFileName(path + suffix), false));
                }
            }
            return new WadGroup(host, parts, null);
        };
    }
}
