using DWSSK.Formats.Core;
namespace DWSSK.Formats.Wad;
internal sealed class WadEntry
{
    public required string File { get; set; }
    public required string Path { get; set; }
    public uint Hash { get; set; }
    public int Type { get; set; }
    public string? StoredPath { get; set; }
    public int? AliasType { get; set; }
    public int AliasEntry { get; set; } = -1;
    public string AliasFile { get; set; } = string.Empty;
    public int? AliasOffset { get; set; }
    public uint? AliasPointer { get; set; }
    public Func<byte[]>? Source { get; set; }
    public byte[] Data
    {
        get => field ??= Source?.Invoke() ?? [];
        set;
    }
    public int Revision { get; set; }
    public bool IsAlias => Type == WadArchive.AliasType;
    public string Extension => WadArchive.ExtensionOf(File);
    public JObj ToManifest()
    {
        JObj m = new()
        {
            ["file"] = File,
            ["path"] = Path,
            ["hash"] = (long)Hash,
            ["type"] = (long)Type,
        };
        if (StoredPath is not null)
        {
            m["stored_path"] = StoredPath;
        }
        if (IsAlias)
        {
            m["alias_type"] = (long)(AliasType ?? 0);
            if (AliasEntry >= 0)
            {
                JObj target = new()
                {
                    ["entry"] = (long)AliasEntry,
                    ["file"] = AliasFile,
                };
                if (AliasOffset is int offset)
                {
                    target["offset"] = (long)offset;
                }
                m["alias_of"] = target;
            }
            else
            {
                m["alias_pointer"] = (long)(AliasPointer ?? 0);
            }
        }
        return m;
    }
}
internal sealed class WadArchive
{
    public const int Header = 0x80;
    public const int EntrySize = 0xC0;
    public const int Buckets = 1024;
    public const int First = Header + (4 * Buckets);
    public const int AliasType = 25;
    private WadArchive(Platform platform, uint alignment, List<WadEntry> entries, uint crc)
    {
        Platform = platform;
        Alignment = alignment;
        Entries = entries;
        Crc = crc;
    }
    public Platform Platform { get; }
    public uint Alignment { get; }
    public List<WadEntry> Entries { get; private set; }
    public uint Crc { get; private set; }
    public int Revision { get; private set; }
    public bool IsDirty { get; private set; }
    public event Action? Changed;
    public static bool IsWad(ReadOnlySpan<byte> head)
    {
        return head.Length >= 4 && head[..4].SequenceEqual("HIG!"u8);
    }
    public static string ExtensionOf(string file)
    {
        int slash = file.LastIndexOf('/');
        string name = slash < 0 ? file : file[(slash + 1)..];
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }
    public static byte[] ReadImage(ReadOnlySpan<byte> raw)
    {
        if (!IsWad(raw))
        {
            throw new InvalidDataException("not a HIG! container");
        }
        uint crc = Bin.U32(raw, 0x48, false);
        int dsize = checked((int)Bin.U32(raw, 0x50, false));
        int csize = checked((int)Bin.U32(raw, 0x54, false));
        ReadOnlySpan<byte> comp = Bin.Slice(raw, Header, Header + csize);
        byte[] image = new byte[Header + dsize];
        raw[..Header].CopyTo(image);
        Tjzip.Decompress(comp, dsize).CopyTo(image, Header);
        return Hashes.Crc32(image.AsSpan(Header), 0xFFFFFFFF) != crc
            ? throw new InvalidDataException("HIG! container CRC mismatch: the payload is damaged")
            : image;
    }
    public static List<int> EntryOffsets(ReadOnlySpan<byte> img)
    {
        HashSet<int> found = [];
        for (int pass = 0; pass < 2; pass++)
        {
            int link = pass == 0 ? 0x6C : 0x70;
            int count = pass == 0 ? Buckets : 17;
            int at = pass == 0 ? Header : 4;
            HashSet<int> seen = [];
            for (int k = 0; k < count; k++)
            {
                int e = (int)Bin.U32(img, at + (4 * k), false);
                while (e != 0 && seen.Add(e))
                {
                    e = (int)Bin.U32(img, e + link, false);
                }
            }
            found.UnionWith(seen);
        }
        List<int> sorted = [.. found];
        sorted.Sort();
        return sorted;
    }
    private readonly record struct Raw(int Offset, uint Hash, string Path, int Type, int Size, int Index);
    private static Raw Parse(ReadOnlySpan<byte> img, int e)
    {
        Bin.Need(img, e, 0x78);
        ReadOnlySpan<byte> name = img.Slice(e + 4, 96);
        int nul = name.IndexOf((byte)0);
        return new Raw(
            e,
            Bin.U32(img, e, false),
            Bin.Latin1(nul < 0 ? name : name[..nul]),
            (int)Bin.U32(img, e + 0x64, false),
            (int)Bin.U32(img, e + 0x68, false),
            (int)Bin.U32(img, e + 0x74, false));
    }
    public static string RelPath(string path)
    {
        string[] parts = path.Split('\\');
        return parts.Length > 5 && Py.Upper(parts[3]) == "DATA"
            ? string.Join('/', parts[5..])
            : path.Replace(":", string.Empty, StringComparison.Ordinal).Replace('\\', '/');
    }
    public static uint PathHash(string path)
    {
        byte[] name = Bin.Latin1(RelPath(path));
        for (int i = 0; i < name.Length; i++)
        {
            name[i] = name[i] switch
            {
                >= (byte)'a' and <= (byte)'z' => (byte)(name[i] - 0x20),
                (byte)'/' => (byte)'\\',
                _ => name[i],
            };
        }
        return Hashes.Crc32(name);
    }
    private static string RecoverName(string path, uint want, List<string> known)
    {
        string rel = RelPath(path);
        string prefix = path[..(path.Length - rel.Length)];
        List<string> names = [.. known, ".gdt.piece", ".col.piece", ".tuning.p2b", ".animpack", ".htx"];
        HashSet<string> tails = new(StringComparer.Ordinal);
        foreach (string k in names)
        {
            for (int i = Math.Max(0, k.Length - 32); i < k.Length; i++)
            {
                _ = tails.Add(k[i..]);
            }
        }
        foreach (string t in tails.OrderBy(t => t.Length))
        {
            string candidate = prefix + rel + t.Replace('/', '\\');
            if (PathHash(candidate) == want)
            {
                return candidate;
            }
        }
        return path;
    }
    public static int Bucket(uint hash, int type)
    {
        return (int)((hash ^ (uint)(33 * type)) & (Buckets - 1));
    }
    public static (int Align, int Bias) DataAlignment(Platform platform, int type)
    {
        if (platform == Platform.PS3)
        {
            return type == 10 ? (64, 0) : (128, 0);
        }
        if (type is 9 or 13 or 15 or 16)
        {
            return platform switch
            {
                Platform.N3ds => (128, 0),
                Platform.Xbox360 => (0x1000, 0x80),
                _ => (64, 0),
            };
        }
        return (64, 0);
    }
    public static Platform? PlatformOfPath(string path)
    {
        string[] parts = path.Split('\\');
        if (parts.Length <= 5 || Py.Upper(parts[3]) != "DATA")
        {
            return null;
        }
        return Py.Upper(parts[4]) switch
        {
            "PS3" => Platform.PS3,
            "WII" => Platform.Wii,
            "360" => Platform.Xbox360,
            "3DS" => Platform.N3ds,
            _ => null,
        };
    }
    public static WadArchive Read(ReadOnlySpan<byte> file, Platform? hint = null)
    {
        byte[] img = ReadImage(file);
        return FromImage(img, hint);
    }
    public static WadArchive FromImage(byte[] img, Platform? hint = null)
    {
        Dictionary<int, int> bucketOf = [];
        for (int b = 0; b < Buckets; b++)
        {
            int e = (int)Bin.U32(img, Header + (4 * b), false);
            HashSet<int> guard = [];
            while (e != 0 && guard.Add(e))
            {
                bucketOf[e] = b;
                e = (int)Bin.U32(img, e + 0x6C, false);
            }
        }
        List<int> offsets = EntryOffsets(img);
        List<Raw> ents = [.. offsets.Select(e => Parse(img, e))];
        List<string> known = [.. ents.Where(x => PathHash(x.Path) == x.Hash).Select(x => RelPath(x.Path))];
        List<WadEntry> entries = new(ents.Count);
        for (int i = 0; i < ents.Count; i++)
        {
            Raw x = ents[i];
            if (x.Index != i)
            {
                throw new InvalidDataException("HIG! entries are not in index order");
            }
            string full = PathHash(x.Path) == x.Hash ? x.Path : RecoverName(x.Path, x.Hash, known);
            WadEntry m = new()
            {
                File = RelPath(full),
                Path = full,
                Hash = x.Hash,
                Type = x.Type,
            };
            if ((full.Length > 95 ? full[..95] : full) != x.Path)
            {
                m.StoredPath = x.Path;
            }
            if (x.Type == AliasType)
            {
                m.AliasType = (int)(((uint)bucketOf.GetValueOrDefault(x.Offset) ^ x.Hash) & (Buckets - 1)) / 33;
                uint ptr = Bin.U32(img, x.Offset + 0x84, false);
                int j = ents.FindIndex(y => y.Offset == ptr + First);
                if (j >= 0)
                {
                    m.AliasEntry = j;
                    m.AliasFile = RelPath(ents[j].Path);
                }
                else
                {
                    m.AliasPointer = ptr;
                }
            }
            else
            {
                m.Data = Bin.Slice(img, x.Offset + EntrySize, x.Offset + EntrySize + x.Size).ToArray();
            }
            entries.Add(m);
        }
        uint alignment = Bin.U32(img, 0x58, false);
        Platform platform = entries.Select(e => PlatformOfPath(e.Path)).FirstOrDefault(p => p is not null)
            ?? hint
            ?? alignment switch
            {
                0x40 => Platform.Wii,
                0x1000 => Platform.Xbox360,
                _ => Platform.PS3,
            };
        return new WadArchive(platform, alignment, entries, Bin.U32(img, 0x48, false));
    }
    public JObj Manifest()
    {
        WadArchive fresh = FromImage(BuildImage(), Platform);
        return new JObj
        {
            ["platform"] = Platform.Name(),
            ["alignment"] = (long)Alignment,
            ["entries"] = J.List(fresh.Entries.Select(e => (object?)e.ToManifest())),
        };
    }
    public static List<WadEntry> EntriesFromManifest(JObj man, Func<string, byte[]> read)
    {
        List<WadEntry> list = [];
        foreach (object? item in J.A(man["entries"]))
        {
            JObj m = J.O(item);
            WadEntry e = new()
            {
                File = J.S(m["file"]),
                Path = J.S(m["path"]),
                Hash = J.U(m["hash"]),
                Type = J.I(m["type"]),
                StoredPath = m.Get("stored_path") as string,
            };
            if (m.Has("alias_type"))
            {
                e.AliasType = J.I(m["alias_type"]);
            }
            if (m.Get("alias_of") is JObj a)
            {
                e.AliasEntry = J.I(a["entry"]);
                e.AliasFile = J.S(a["file"]);
                e.AliasOffset = a.Has("offset") ? J.I(a["offset"]) : null;
            }
            else if (m.Has("alias_pointer"))
            {
                e.AliasPointer = J.U(m["alias_pointer"]);
            }
            if (!e.IsAlias)
            {
                string file = e.File;
                e.Source = () => read(file);
            }
            list.Add(e);
        }
        return list;
    }
    public static WadArchive FromManifest(JObj man, Func<string, byte[]> read)
    {
        return new WadArchive(
            Platforms.Parse(J.S(man["platform"])), J.U(man["alignment"]), EntriesFromManifest(man, read), 0);
    }
    public string PathPrefix()
    {
        if (Entries.Count == 0)
        {
            return string.Empty;
        }
        WadEntry first = Entries[0];
        return first.Path[..(first.Path.Length - first.File.Length)];
    }
    public int? TypeForName(string fileName)
    {
        Dictionary<string, int> exts = [];
        foreach (WadEntry m in Entries)
        {
            string name = m.File[(m.File.LastIndexOf('/') + 1)..];
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if (m.Type != AliasType && dot >= 0)
            {
                _ = exts.TryAdd(name[dot..].ToLowerInvariant(), m.Type);
            }
        }
        for (int i = 0; i < fileName.Length; i++)
        {
            if (fileName[i] == '.' && exts.TryGetValue(fileName[i..].ToLowerInvariant(), out int type))
            {
                return type;
            }
        }
        return null;
    }
    public WadEntry NewEntry(string relative, byte[] data)
    {
        string name = relative[(relative.LastIndexOf('/') + 1)..];
        int type = TypeForName(name)
            ?? throw new InvalidDataException(
                $"{relative}: no file with this extension in the container, so its entry type is unknown");
        string path = PathPrefix() + relative.Replace('/', '\\');
        return new WadEntry { File = relative, Path = path, Hash = PathHash(path), Type = type, Data = data };
    }
    public byte[] BuildImage()
    {
        return BuildImage(Platform, Alignment, Entries);
    }
    public static byte[] BuildImage(Platform platform, uint alignment, IReadOnlyList<WadEntry> ents)
    {
        int n = ents.Count;
        int[] eff = new int[n];
        int[] offs = new int[n];
        long cur = First;
        for (int i = 0; i < n; i++)
        {
            WadEntry m = ents[i];
            eff[i] = m.IsAlias && m.AliasType is { } at ? at : m.Type;
            (int align, int bias) = DataAlignment(platform, eff[i]);
            long start = (cur + EntrySize + 63) / 64 * 64;
            while ((start + bias) % align != 0)
            {
                start += 64;
            }
            offs[i] = checked((int)(start - EntrySize));
            cur = start + (m.IsAlias ? 0 : m.Data.Length);
        }
        byte[] img = new byte[checked((int)(n > 0 ? cur : First + EntrySize))];
        "HIG!"u8.CopyTo(img);
        Dictionary<int, int> nextBucket = [];
        Dictionary<int, int> nextType = [];
        for (int i = n - 1; i >= 0; i--)
        {
            WadEntry m = ents[i];
            int e = offs[i];
            int b = Bucket(m.Hash, eff[i]);
            byte[] stored = Bin.Latin1(m.StoredPath ?? (m.Path.Length > 95 ? m.Path[..95] : m.Path));
            Span<byte> head = img.AsSpan(e, EntrySize);
            Bin.PutU32(head, 0, m.Hash, false);
            stored.AsSpan(0, Math.Min(96, stored.Length)).CopyTo(head[4..]);
            Bin.PutU32(head, 0x64, (uint)m.Type, false);
            Bin.PutU32(head, 0x68, (uint)(m.IsAlias ? 0 : m.Data.Length), false);
            Bin.PutU32(head, 0x6C, (uint)nextBucket.GetValueOrDefault(b), false);
            Bin.PutU32(head, 0x70, (uint)nextType.GetValueOrDefault(eff[i]), false);
            Bin.PutU32(head, 0x74, (uint)i, false);
            nextBucket[b] = e;
            nextType[eff[i]] = e;
            if (m.IsAlias)
            {
                uint ptr = m.AliasEntry < 0 ? m.AliasPointer ?? 0
                    : m.AliasOffset is int legacy ? (uint)(offs[m.AliasEntry] + EntrySize + legacy)
                    : (uint)(offs[m.AliasEntry] - First);
                Bin.PutU32(head, 0x84, ptr, false);
            }
            else
            {
                m.Data.CopyTo(img, e + EntrySize);
            }
        }
        foreach ((int b, int e) in nextBucket)
        {
            Bin.PutU32(img, Header + (4 * b), (uint)e, false);
        }
        foreach ((int t, int e) in nextType)
        {
            if (t is >= 0 and < 17)
            {
                Bin.PutU32(img, 4 + (4 * t), (uint)e, false);
            }
        }
        Bin.PutU32(img, 0x4C, 9, false);
        Bin.PutU32(img, 0x50, (uint)(img.Length - Header), false);
        Bin.PutU32(img, 0x54, 0, false);
        Bin.PutU32(img, 0x58, alignment, false);
        Bin.PutU32(img, 0x48, Hashes.Crc32(img.AsSpan(Header), 0xFFFFFFFF), false);
        return img;
    }
    public static byte[] CompressImage(byte[] img)
    {
        byte[] comp = Tjzip.Compress(img.AsSpan(Header));
        byte[] file = new byte[Header + comp.Length];
        img.AsSpan(0, Header).CopyTo(file);
        Bin.PutU32(file, 0x54, (uint)comp.Length, false);
        comp.CopyTo(file, Header);
        return file;
    }
    public byte[] Build()
    {
        byte[] img = BuildImage();
        Crc = Bin.U32(img, 0x48, false);
        return CompressImage(img);
    }
    public void Replace(WadEntry entry, byte[] data)
    {
        if (entry.IsAlias)
        {
            throw new InvalidOperationException($"{entry.File} is an alias of another entry and holds no data of its own");
        }
        entry.Data = data;
        entry.Revision++;
        Touch();
    }
    public void Add(WadEntry entry)
    {
        Entries.Add(entry);
        Touch();
    }
    public void ReplaceAll(List<WadEntry> entries)
    {
        Entries = entries;
        Touch();
    }
    public void Touch()
    {
        Revision++;
        IsDirty = true;
        Changed?.Invoke();
    }
    public void MarkSaved()
    {
        IsDirty = false;
    }
}
