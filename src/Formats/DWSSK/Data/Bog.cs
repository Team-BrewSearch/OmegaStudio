using System.Text.RegularExpressions;
using DWSSK.Formats.Core;

namespace DWSSK.Formats.Data;

internal static partial class Bog
{
    private sealed record Section(string Key, int Size, string Fmt, string[] Names);

    private static readonly SortedDictionary<int, Section> s_layout = new()
    {
        [0] = new("lights", 24, "3fIII", ["direction:3", "unknown_0c", "colour", "colour_2"]),
        [1] = new("spawners", 88, "16f6I", ["matrix:16", "hash", "kind", "unknown_48", "template", "data", "data_size"]),
        [2] = new("components", 24, "6I", ["name_offset", "defaults", "hash", "layout", "size", "use_count"]),
        [3] = new("classes", 28, "7I", ["name_offset", "components", "component_count", "hash", "entities", "entity_count", "placed_count"]),
        [4] = new("templates", 52, "13I", ["name_offset", "hash", "class", "resources:6", "spawners", "spawner_count", "unknown_2c", "unknown_30"]),
        [5] = new("entities", 88, "2I16f4I", ["name_offset", "hash", "matrix:16", "template", "data", "id", "flags"]),
        [6] = new("path_nodes", 168, "2I16f24I", ["name_offset", "hash", "matrix:16", "links:24"]),
        [10] = new("triggers", 144, "I16f16f3I", ["name_offset", "matrix:16", "inverse:16", "hash", "unknown_88", "unknown_8c"]),
        [11] = new("points", 16, "3fI", ["position:3", "unknown_0c"]),
        [12] = new("splines", 28, "7I", ["name_offset", "points", "unknown_08", "hash", "leftover_10", "unknown_14", "unknown_18"]),
        [13] = new("tie_groups", 20, "5I", ["hash", "model", "unknown_08", "ties", "tie_count"]),
        [14] = new("ties", 112, "16f12I", ["matrix:16", "unknown_40", "unknown_44", "unknown_48", "unknown_4c", "unknown_50", "unknown_54", "unknown_58", "hash", "unknown_60", "flags", "unknown_68", "unknown_6c"]),
        [15] = new("shrub_layers", 60, "5I7f3I", ["hash", "model", "unknown_08", "shrubs", "shrub_count", "bounds_min:3", "bounds_max:3", "cell_size", "cells_x", "cells_y", "cells"]),
        [16] = new("shrub_cells", 8, "2I", ["start", "end"]),
        [17] = new("shrubs", 80, "16f3If", ["matrix:16", "unknown_40", "unknown_44", "hash", "unknown_4c"]),
    };

    private static readonly Dictionary<object, string> s_componentWords = new()
    {
        ["AnimCamera"] = "BIII", ["AnimGraph"] = "I", ["Animation"] = "If", ["Attach"] = "II", ["Bloom"] = "tfffff",
        ["Button"] = "eIIIIIfI", ["ChildEntity"] = string.Empty, ["CurveFollower"] = "BfIBsefIIII", ["DamageVolume"] = "tBIBI",
        ["DieOnTimer"] = "ffI", ["DirLightRamp"] = "I", ["EffectScript"] = "If", ["Fog"] = "tffB", ["FrontEnd"] = "eeeeeeee",
        ["LinearMotion"] = "IIIIIIIIIIIII", ["Mesh"] = "BBB", ["MessageForwardGroup"] = "eeeIII", ["MovingPopup"] = "eff",
        ["NPC"] = "I", ["PickUp"] = "IBI", ["RaceMgr"] = "IBeeeeeeeeeeeIeeIII", ["RobotHead"] = "fffff", ["Rotator"] = "ffffffB",
        ["SoundPlayer"] = "BetII", ["SpawnAndAttach"] = "I", ["VisibilityRegion"] = new string('B', 16) + "t", ["camera"] = "IfffIIIff",
        [0xD4D09FA1u] = "tBeIIeIIeII", [0x64A3ABFEu] = "IeIIeIIeIIeI" + new string('I', 7), [0x66EDBFB7u] = "Bfff",
        [0x1EB373B4u] = "BtIII", [0x7B295B5Bu] = "eBffffI", [0x4D51CAADu] = "effB", [0xEA48DC4Cu] = "tBeII",
        [0xC146BC77u] = "BffffffII", [0x5F003DF1u] = "I",
    };

    private static readonly Dictionary<char, (int Section, string Key)> s_links = new()
    {
        ['e'] = (5, "entity"),
        ['t'] = (10, "trigger"),
        ['s'] = (12, "spline"),
    };

    private static readonly Dictionary<int, (string Field, int Section)[]> s_pointers = new()
    {
        [1] = [("template", 4)],
        [3] = [("entities", 5)],
        [4] = [("class", 3), ("spawners", 1)],
        [5] = [("template", 4)],
        [12] = [("points", 11)],
        [13] = [("ties", 14)],
        [15] = [("shrubs", 17), ("cells", 16)],
        [16] = [("start", 17), ("end", 17)],
    };

    private static readonly Dictionary<int, string[]> s_floats = new() { [14] = ["unknown_48", "unknown_4c", "unknown_50"] };

    private static readonly Dictionary<uint, string> s_words = BuildWords();

    private static readonly Dictionary<uint, string[]> s_fields = new()
    {
        [Hashes.UpperCrc("Rotator")] = ["localXAxisSpinRate", "localYAxisSpinRate", "localZAxisSpinRate", "worldXAxisSpinRate", "worldYAxisSpinRate", "worldZAxisSpinRate", "rotMode"],
        [Hashes.UpperCrc("DieOnTimer")] = ["lifetime", "fadetime", "unknown_08"],
        [Hashes.UpperCrc("AnimGraph")] = ["initialState"],
    };

    private static Dictionary<uint, string> BuildWords()
    {
        Dictionary<uint, string> d = [];
        foreach ((object k, string v) in s_componentWords)
        {
            d[k is string s ? Hashes.UpperCrc(s) : (uint)k] = v;
        }
        return d;
    }

    public static bool IsBog(ReadOnlySpan<byte> b)
    {
        return b.Length >= 0xB0 && b[..4].SequenceEqual("BOG "u8);
    }

    private static List<(char Type, int Count)> Fmt(string fmt)
    {
        List<(char, int)> o = [];
        int i = 0;
        while (i < fmt.Length)
        {
            int n = 0;
            while (char.IsDigit(fmt[i]))
            {
                n = (n * 10) + (fmt[i] - '0');
                i++;
            }
            o.Add((fmt[i], n == 0 ? 1 : n));
            i++;
        }
        return o;
    }

    private static List<object?> UnpackValues(bool big, string fmt, ReadOnlySpan<byte> b, int o)
    {
        List<object?> vals = [];
        foreach ((char t, int n) in Fmt(fmt))
        {
            for (int k = 0; k < n; k++)
            {
                vals.Add(t == 'f' ? (object)(double)Bin.F32(b, o, big) : (long)Bin.U32(b, o, big));
                o += 4;
            }
        }
        return vals;
    }

    private static JObj UnpackRecord(bool big, int sec, ReadOnlySpan<byte> b, int o)
    {
        Section s = s_layout[sec];
        List<object?> vals = UnpackValues(big, s.Fmt, b, o);
        JObj rec = [];
        int at = 0;
        foreach (string full in s.Names)
        {
            int colon = full.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                int cnt = int.Parse(full[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                rec[full[..colon]] = vals.GetRange(at, cnt);
                at += cnt;
            }
            else
            {
                rec[full] = vals[at++];
            }
        }
        if (s_floats.TryGetValue(sec, out string[]? floats))
        {
            foreach (string n in floats)
            {
                rec[n] = (double)BitConverter.UInt32BitsToSingle((uint)J.L(rec[n]));
            }
        }
        return rec;
    }

    private static byte[] PackRecord(bool big, int sec, JObj rec)
    {
        Section s = s_layout[sec];
        List<object?> vals = [];
        foreach (string full in s.Names)
        {
            int colon = full.IndexOf(':', StringComparison.Ordinal);
            string n = colon >= 0 ? full[..colon] : full;
            object? v = rec[n];
            if (s_floats.TryGetValue(sec, out string[]? floats) && floats.Contains(n))
            {
                v = (long)BitConverter.SingleToUInt32Bits(Py.ToSingle(J.D(v)));
            }
            if (colon >= 0)
            {
                vals.AddRange(J.A(v));
            }
            else
            {
                vals.Add(v);
            }
        }
        ByteWriter o = new(s.Size);
        int at = 0;
        foreach ((char t, int n) in Fmt(s.Fmt))
        {
            for (int k = 0; k < n; k++)
            {
                if (at >= vals.Count)
                {
                    throw new StructException($"pack expected more items for packing a {s.Key} record");
                }
                if (t == 'f')
                {
                    o.F32(J.D(vals[at++]), big);
                }
                else
                {
                    o.U32(J.U(vals[at++]), big);
                }
            }
        }
        if (at != vals.Count)
        {
            throw new StructException($"pack expected {at} items for packing (got {vals.Count})");
        }
        return o.ToArray();
    }

    private static int Align(int n, int a = 16)
    {
        return Bin.CeilDiv(n, a) * a;
    }

    private static (List<int> Offs, int Total) BlobLayout(IEnumerable<int> sizes)
    {
        List<int> offs = [];
        int o = 0;
        foreach (int z in sizes)
        {
            o = z != 0 ? Align(o) : o;
            offs.Add(o);
            o += z;
        }
        return (offs, Align(o));
    }

    private static string? Words(JObj comp)
    {
        return s_words.TryGetValue(J.U(comp["hash"]), out string? w) && 4 * w.Length <= J.L(comp["size"]) ? w : null;
    }

    private static List<object?> Typed(ReadOnlySpan<byte> raw, string words, bool big, Dictionary<int, Dictionary<long, int>> links)
    {
        List<object?> output = [];
        for (int k = 0; k < words.Length; k++)
        {
            char t = words[k];
            ReadOnlySpan<byte> w = raw.Slice(4 * k, 4);
            if (s_links.TryGetValue(t, out (int Section, string Key) link))
            {
                uint v = Bin.U32(w, 0, big);
                output.Add(v != 0 ? new JObj { [link.Key] = (long)links[link.Section][v] } : null);
            }
            else if (t == 'B')
            {
                output.Add(J.Longs(w));
            }
            else if (t == 'f')
            {
                float v = Bin.F32(w, 0, big);
                output.Add(float.IsFinite(v) ? (double)v : new JObj { ["bits"] = (long)Bin.U32(w, 0, big) });
            }
            else
            {
                output.Add((long)Bin.U32(w, 0, big));
            }
        }
        foreach (byte x in raw[(4 * words.Length)..])
        {
            output.Add((long)x);
        }
        return output;
    }

    private static byte[] Untyped(object? vals, string? words, bool big, Func<int, object?, uint> ptr)
    {
        if (vals is string hex)
        {
            return Bin.FromHex(hex);
        }
        List<object?> list = vals is JObj named ? [.. named.Select(kv => kv.Value)] : J.A(vals);
        words ??= string.Empty;
        ByteWriter o = new();
        int count = Math.Min(list.Count, words.Length);
        for (int i = 0; i < count; i++)
        {
            object? v = list[i];
            char t = words[i];
            if (s_links.TryGetValue(t, out (int Section, string Key) link))
            {
                o.U32(v is not null && J.Truthy(v) ? ptr(link.Section, J.O(v)[link.Key]) : 0, big);
            }
            else if (t == 'B')
            {
                foreach (object? x in J.A(v))
                {
                    o.Add(J.U8(x));
                }
            }
            else if (t == 'f' && v is not JObj)
            {
                o.F32(J.D(v), big);
            }
            else
            {
                o.U32(v is JObj bits ? J.U(bits["bits"]) : J.U(v), big);
            }
        }
        for (int i = words.Length; i < list.Count; i++)
        {
            o.Add(J.U8(list[i]));
        }
        return o.ToArray();
    }

    private static int TieExtra(ReadOnlySpan<byte> b, int o)
    {
        ReadOnlySpan<byte> f = Bin.Slice(b, o + 0x64, o + 0x68);
        if (f.Length < 4)
        {
            throw new PyIndexException("index out of range");
        }
        return f[3] == 0xFF ? f[2] : f[0] == 0xFF ? f[1] : 0;
    }

    private static long? Index(long ptr, List<int> starts, int end)
    {
        int i = starts.IndexOf((int)ptr);
        if (i >= 0 && ptr <= int.MaxValue)
        {
            return (long)i;
        }
        if (ptr == end)
        {
            return (long)starts.Count;
        }
        return ptr == 0 ? null : throw new InvalidDataException($"offset 0x{ptr:x} is not a record");
    }

    public static JObj Read(byte[] b)
    {
        bool big = b.Length > 4 && b[4] == 0;
        uint version = Bin.U32(b, 4, big);
        uint size = Bin.U32(b, 8, big);
        uint level = Bin.U32(b, 12, big);
        uint colour = Bin.U32(b, 16, big);
        uint z1 = Bin.U32(b, 20, big);
        uint z2 = Bin.U32(b, 24, big);
        if (!b.AsSpan(0, 4).SequenceEqual("BOG "u8) || version != 72 || size != b.Length || z1 != 0 || z2 != 0
            || Bin.AnyNonZero(Bin.Slice(b, 0xAC, 0xB0)))
        {
            throw new InvalidDataException("not a version 72 BOG");
        }
        (uint Off, uint Cnt)[] table = new (uint, uint)[18];
        for (int i = 0; i < 18; i++)
        {
            table[i] = (Bin.U32(b, 0x1C + (i * 8), big), Bin.U32(b, 0x20 + (i * 8), big));
        }
        if (table[7] != (0, 0) || table[8] != (0, 0) || table[9] != (0, 0))
        {
            throw new InvalidDataException("sections 7-9 are not empty");
        }
        JObj doc = new() { ["endian"] = big ? "big" : "little", ["level_hash"] = (long)level, ["colour"] = (long)colour };
        Dictionary<int, List<int>> starts = [];
        Dictionary<int, int> ends = [];
        foreach ((int sec, Section s) in s_layout)
        {
            (uint off, uint cnt) = table[sec];
            List<object?> recs = [];
            List<int> offs = [];
            int o = (int)off;
            for (uint k = 0; k < cnt; k++)
            {
                JObj r = UnpackRecord(big, sec, b, o);
                offs.Add(o);
                o += s.Size;
                if (sec == 14)
                {
                    int n = TieExtra(b, offs[^1]);
                    List<object?> extra = [];
                    for (int x = 0; x < n; x++)
                    {
                        extra.Add(J.Longs(Bin.U32s(b, o + (12 * x), 3, big)));
                    }
                    r["extra"] = extra;
                    o += 12 * n;
                }
                recs.Add(r);
            }
            doc[s.Key] = recs;
            starts[sec] = offs;
            ends[sec] = o;
        }
        foreach ((int sec, (string Field, int Section)[] fields) in s_pointers)
        {
            foreach (object? ro in J.A(doc[s_layout[sec].Key]))
            {
                JObj r = J.O(ro);
                foreach ((string f, int t) in fields)
                {
                    r[f] = Index(J.L(r[f]), starts[t], ends[t]);
                }
            }
        }
        List<object?> comps = J.A(doc["components"]);
        string?[] words = [.. comps.Select(c => Words(J.O(c)))];
        Dictionary<int, Dictionary<long, int>> links = [];
        foreach ((int sec, _) in s_links.Values)
        {
            Dictionary<long, int> map = [];
            for (int i = 0; i < starts[sec].Count; i++)
            {
                map[starts[sec][i]] = i;
            }
            links[sec] = map;
        }
        object? Value(ReadOnlySpan<byte> raw, int k)
        {
            if (words[k] is null)
            {
                return Bin.Hex(raw);
            }
            List<object?> vals = Typed(raw, words[k]!, big, links);
            return s_fields.TryGetValue(J.U(J.O(comps[k])["hash"]), out string[]? names) && names.Length == vals.Count
                ? Zip(names, vals)
                : vals;
        }
        for (int k = 0; k < comps.Count; k++)
        {
            JObj c = J.O(comps[k]);
            long def = J.L(c["defaults"]);
            int csize = (int)J.L(c["size"]);
            c["defaults"] = def != 0 ? Value(Bin.Slice(b, (int)def, (int)def + csize), k) : null;
        }
        List<object?> classes = J.A(doc["classes"]);
        foreach (object? co in classes)
        {
            JObj c = J.O(co);
            int count = (int)J.L(c.Pop("component_count"));
            uint[] lst = Bin.U32s(b, (int)J.L(c["components"]), count, big);
            c["components"] = J.Longs(lst.Select(x =>
            {
                int i = starts[2].IndexOf((int)x);
                return i >= 0 ? (long)i : throw new InvalidDataException($"{x} is not in list");
            }));
        }
        Dictionary<int, int> owner = [];
        for (int ci = 0; ci < classes.Count; ci++)
        {
            JObj c = J.O(classes[ci]);
            for (int k = 0; k < J.L(c["entity_count"]); k++)
            {
                owner[(int)(J.L(c["entities"]) + k)] = ci;
            }
        }
        object? Blob(long at, List<int> ks)
        {
            if (at == 0)
            {
                return null;
            }
            (List<int> offs, _) = BlobLayout(ks.Select(k => (int)J.L(J.O(comps[k])["size"])));
            List<object?> output = [];
            for (int i = 0; i < ks.Count; i++)
            {
                int k = ks[i];
                int sz = (int)J.L(J.O(comps[k])["size"]);
                output.Add(sz != 0 ? Value(Bin.Slice(b, (int)at + offs[i], (int)at + offs[i] + sz), k) : null);
            }
            return output;
        }
        List<object?> entities = J.A(doc["entities"]);
        for (int i = 0; i < entities.Count; i++)
        {
            JObj e = J.O(entities[i]);
            long at = J.L(e["data"]);
            e["data"] = Blob(at, ClassComponents(classes, owner, i, at));
        }
        List<object?> templates = J.A(doc["templates"]);
        foreach (object? so in J.A(doc["spawners"]))
        {
            JObj s = J.O(so);
            s.Remove("data_size");
            long at = J.L(s["data"]);
            List<int> ks = at != 0
                ? [.. J.A(J.O(classes[J.I(J.O(templates[J.I(s["template"])])["class"])])["components"]).Select(J.I)]
                : [];
            s["data"] = Blob(at, ks);
        }
        return doc;
    }

    private static List<int> ClassComponents(List<object?> classes, Dictionary<int, int> owner, int entity, long at)
    {
        if (!owner.TryGetValue(entity, out int ci))
        {
            throw new KeyNotFoundException($"{entity}");
        }
        return [.. J.A(J.O(classes[ci])["components"]).Select(J.I)];
    }

    private static JObj Zip(string[] names, List<object?> vals)
    {
        JObj o = [];
        for (int i = 0; i < names.Length; i++)
        {
            o[names[i]] = vals[i];
        }
        return o;
    }

    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        List<object?> comps = J.A(doc["components"]);
        List<object?> classes = J.A(doc["classes"]);
        List<object?> templates = J.A(doc["templates"]);
        List<object?> entities = J.A(doc["entities"]);
        List<object?> spawners = J.A(doc["spawners"]);
        string?[] words = [.. comps.Select(c => Words(J.O(c)))];
        Dictionary<int, int> owner = [];
        for (int ci = 0; ci < classes.Count; ci++)
        {
            JObj c = J.O(classes[ci]);
            for (int k = 0; k < J.L(c["entity_count"]); k++)
            {
                owner[(int)(J.L(c["entities"]) + k)] = ci;
            }
        }
        List<int> SpawnerClass(JObj s)
        {
            return [.. J.A(J.O(classes[J.I(J.O(templates[J.I(s["template"])])["class"])])["components"]).Select(J.I)];
        }
        int BlobSize(List<int> ks)
        {
            return BlobLayout(ks.Select(k => (int)J.L(J.O(comps[k])["size"]))).Total;
        }
        int o = 0xB0;
        Dictionary<int, List<int>> starts = [];
        Dictionary<int, int> ends = [];
        Dictionary<(int, int), int> pools = [];
        int Place(int n)
        {
            int at = o;
            o += n;
            return at;
        }
        foreach ((int sec, Section s) in s_layout)
        {
            List<object?> recs = J.A(doc[s.Key]);
            o = Align(o);
            starts[sec] = [];
            foreach (object? ro in recs)
            {
                JObj r = J.O(ro);
                starts[sec].Add(Place(s.Size + (12 * (r.Get("extra") is List<object?> ex ? ex.Count : 0))));
            }
            ends[sec] = o;
            if (sec == 1)
            {
                for (int i = 0; i < recs.Count; i++)
                {
                    JObj r = J.O(recs[i]);
                    if (r["data"] is not null)
                    {
                        o = Align(o);
                        pools[(1, i)] = Place(BlobSize(SpawnerClass(r)));
                    }
                }
            }
            if (sec == 4)
            {
                o = Align(o);
                for (int i = 0; i < classes.Count; i++)
                {
                    pools[(3, i)] = Place(4 * J.A(J.O(classes[i])["components"]).Count);
                }
                for (int i = 0; i < comps.Count; i++)
                {
                    JObj c = J.O(comps[i]);
                    if (c["defaults"] is not null)
                    {
                        o = Align(o);
                        pools[(2, i)] = Place((int)J.L(c["size"]));
                    }
                }
                for (int i = 0; i < entities.Count; i++)
                {
                    if (J.O(entities[i])["data"] is not null)
                    {
                        o = Align(o);
                        pools[(5, i)] = Place(BlobSize([.. J.A(J.O(classes[owner[i]])["components"]).Select(J.I)]));
                    }
                }
                o = Align(o);
            }
        }
        int total = o;
        byte[] buf = new byte[total];
        uint Ptr(int sec, object? idx)
        {
            if (idx is null)
            {
                return 0;
            }
            long i = J.L(idx);
            return i < starts[sec].Count ? (uint)starts[sec][(int)i] : (uint)ends[sec];
        }
        foreach ((int sec, Section s) in s_layout)
        {
            List<object?> recs = J.A(doc[s.Key]);
            for (int i = 0; i < recs.Count; i++)
            {
                JObj r = J.O(recs[i]).Copy();
                if (s_pointers.TryGetValue(sec, out (string Field, int Section)[]? fields))
                {
                    foreach ((string f, int t) in fields)
                    {
                        r[f] = (long)Ptr(t, r[f]);
                    }
                }
                if (sec == 1)
                {
                    r["data_size"] = r["data"] is not null ? (long)BlobSize(SpawnerClass(J.O(spawners[i]))) : 0L;
                    r["data"] = (long)pools.GetValueOrDefault((1, i));
                }
                else if (sec == 2)
                {
                    r["defaults"] = (long)pools.GetValueOrDefault((2, i));
                }
                else if (sec == 3)
                {
                    r["component_count"] = (long)J.A(r["components"]).Count;
                    r["components"] = (long)pools[(3, i)];
                }
                else if (sec == 5)
                {
                    r["data"] = (long)pools.GetValueOrDefault((5, i));
                }
                int at = starts[sec][i];
                PackRecord(big, sec, r).CopyTo(buf, at);
                if (r.Get("extra") is List<object?> extra)
                {
                    for (int k = 0; k < extra.Count; k++)
                    {
                        List<object?> x = J.A(extra[k]);
                        if (x.Count != 3)
                        {
                            throw new StructException("pack expected 3 items for packing");
                        }
                        for (int c = 0; c < 3; c++)
                        {
                            Bin.PutU32(buf, at + s.Size + (12 * k) + (4 * c), J.U(x[c]), big);
                        }
                    }
                }
            }
        }
        for (int i = 0; i < classes.Count; i++)
        {
            List<object?> cl = J.A(J.O(classes[i])["components"]);
            int at = pools[(3, i)];
            for (int k = 0; k < cl.Count; k++)
            {
                Bin.PutU32(buf, at + (4 * k), (uint)starts[2][J.I(cl[k])], big);
            }
        }
        for (int i = 0; i < comps.Count; i++)
        {
            JObj c = J.O(comps[i]);
            if (c["defaults"] is not null)
            {
                byte[] raw = Untyped(c["defaults"], words[i], big, Ptr);
                Splice(buf, pools[(2, i)], (int)J.L(c["size"]), raw);
            }
        }
        void Put(int at, List<int> ks, List<object?> data)
        {
            (List<int> offs, _) = BlobLayout(ks.Select(k => (int)J.L(J.O(comps[k])["size"])));
            for (int i = 0; i < Math.Min(Math.Min(offs.Count, ks.Count), data.Count); i++)
            {
                if (data[i] is not null)
                {
                    byte[] raw = Untyped(data[i], words[ks[i]], big, Ptr);
                    Splice(buf, at + offs[i], raw.Length, raw);
                }
            }
        }
        for (int i = 0; i < entities.Count; i++)
        {
            JObj e = J.O(entities[i]);
            if (e["data"] is List<object?> data)
            {
                Put(pools[(5, i)], [.. J.A(J.O(classes[owner[i]])["components"]).Select(J.I)], data);
            }
        }
        for (int i = 0; i < spawners.Count; i++)
        {
            JObj s = J.O(spawners[i]);
            if (s["data"] is List<object?> data)
            {
                Put(pools[(1, i)], SpawnerClass(s), data);
            }
        }
        "BOG "u8.CopyTo(buf);
        Bin.PutU32(buf, 4, 72, big);
        Bin.PutU32(buf, 8, (uint)total, big);
        Bin.PutU32(buf, 12, J.U(doc["level_hash"]), big);
        Bin.PutU32(buf, 16, J.U(doc["colour"]), big);
        Bin.PutU32(buf, 20, 0, big);
        Bin.PutU32(buf, 24, 0, big);
        foreach ((int sec, Section s) in s_layout)
        {
            List<object?> recs = J.A(doc[s.Key]);
            if (recs.Count > 0 || sec <= 10)
            {
                Bin.PutU32(buf, 0x1C + (sec * 8), (uint)(starts[sec].Count > 0 ? starts[sec][0] : ends[sec]), big);
                Bin.PutU32(buf, 0x20 + (sec * 8), (uint)recs.Count, big);
            }
        }
        return buf;
    }

    private static void Splice(byte[] buf, int at, int size, byte[] raw)
    {
        if (raw.Length != size)
        {
            throw new InvalidDataException($"component data of {raw.Length} bytes in a slot of {size}");
        }
        raw.CopyTo(buf, at);
    }

    [GeneratedRegex(@"\[\s+((?:[^\[\]{}]|\[[^\[\]{}]*\])+?)\s+\]")]
    private static partial Regex Lists();

    public static string Dumps(JObj doc)
    {
        string s = PyJson.Dumps(doc, 1);
        return Lists().Replace(s, m => "[" + string.Join(' ', Py.SplitWhitespace(m.Groups[1].Value)) + "]");
    }

    private static readonly (string Key, string[] Fields)[] s_hashes =
    [
        ("spawners", ["hash", "kind"]), ("components", ["hash"]), ("classes", ["hash"]),
        ("templates", ["hash", "resources"]), ("entities", ["hash"]), ("path_nodes", ["hash"]),
        ("triggers", ["hash"]), ("splines", ["hash"]), ("tie_groups", ["hash", "model"]),
        ("shrub_layers", ["hash", "model"]),
    ];

    public static void AddNames(JObj doc, IEnumerable<string> names)
    {
        Dictionary<long, string> table = [];
        List<string> list = [.. names];
        foreach (string n in list)
        {
            if (Hashes.TryUpperCrc(n, out uint h))
            {
                table[h] = n;
            }
        }
        foreach (string n in list)
        {
            if (n.All(c => c <= 0xFF))
            {
                table[Hashes.Crc32(System.Text.Encoding.Latin1.GetBytes(n))] = n;
            }
        }
        List<object?>? Words(object? v)
        {
            List<object?> vals = v is JObj o ? [.. o.Select(kv => kv.Value)] : v as List<object?> ?? [];
            List<object?> output = [.. vals.Select(x => x is long l && table.TryGetValue(l, out string? s) ? (object?)s : null)];
            return output.Any(x => x is string { Length: > 0 }) ? output : null;
        }
        foreach (object? co in J.A(doc["components"]))
        {
            JObj c = J.O(co);
            if (Words(c["defaults"]) is { } dn)
            {
                c["defaults_names"] = dn;
            }
        }
        foreach (string key in new[] { "entities", "spawners" })
        {
            foreach (object? ro in J.A(doc[key]))
            {
                JObj r = J.O(ro);
                List<object?> ns = [.. (r["data"] as List<object?> ?? []).Select(v => (object?)Words(v))];
                if (ns.Any(x => x is List<object?>))
                {
                    r["data_names"] = ns;
                }
            }
        }
        foreach ((string key, string[] fields) in s_hashes)
        {
            foreach (object? ro in J.A(doc[key]))
            {
                JObj r = J.O(ro);
                foreach (string f in fields)
                {
                    object? v = r[f];
                    if (v is List<object?> lst)
                    {
                        if (lst.Any(x => x is long l && table.ContainsKey(l)))
                        {
                            r[f + "_names"] = J.List(lst.Select(x => x is long l && table.TryGetValue(l, out string? s) ? (object?)s : null));
                        }
                    }
                    else if (v is long l && table.TryGetValue(l, out string? s))
                    {
                        r[f + "_name"] = s;
                    }
                }
            }
        }
    }

    public static JObj ToGltf(JObj doc)
    {
        List<object?> nodes = [];
        List<object?> groups = [];
        static string Nm(JObj r, int i, string kind)
        {
            return r.Get("hash_name") is string { Length: > 0 } s ? s : $"{kind}_{i}_{Py.Hex8((uint)J.L(r.Get("hash", 0L)))}";
        }
        void Group(string name, List<object?> children)
        {
            nodes.Add(new JObj { ["name"] = name, ["children"] = children });
            groups.Add((long)(nodes.Count - 1));
        }
        List<object?> tmpl = J.A(doc["templates"]);
        List<object?> cls = J.A(doc["classes"]);
        foreach ((string kind, string key) in new[] { ("entity", "entities"), ("path_node", "path_nodes"), ("trigger", "triggers") })
        {
            List<object?> kids = [];
            List<object?> recs = J.A(doc[key]);
            for (int i = 0; i < recs.Count; i++)
            {
                JObj r = J.O(recs[i]);
                JObj n = new() { ["name"] = Nm(r, i, kind), ["matrix"] = r["matrix"] };
                if (kind == "entity" && r["template"] is not null)
                {
                    JObj t = J.O(tmpl[J.I(r["template"])]);
                    n["extras"] = new JObj
                    {
                        ["template"] = t.Get("hash_name") is string tn ? tn : Py.Hex8(J.U(t["hash"])),
                        ["class"] = J.O(cls[J.I(t["class"])]).Get("hash_name"),
                        ["resources"] = t.Get("resources_names"),
                    };
                }
                nodes.Add(n);
                kids.Add((long)(nodes.Count - 1));
            }
            Group(key, kids);
        }
        foreach ((string key, string rkey, string first, string count) in new[] { ("tie_groups", "ties", "ties", "tie_count"), ("shrub_layers", "shrubs", "shrubs", "shrub_count") })
        {
            List<object?> kids = [];
            List<object?> recs = J.A(doc[rkey]);
            foreach (object? go in J.A(doc[key]))
            {
                JObj g = J.O(go);
                string model = g.Get("model_name") is string mn ? mn : Py.Hex8(J.U(g["model"]));
                long start = J.L(g[first]);
                for (long k = start; k < start + J.L(g[count]); k++)
                {
                    nodes.Add(new JObj
                    {
                        ["name"] = $"{model.Split('\\')[^1]}_{k}",
                        ["matrix"] = J.O(recs[(int)k])["matrix"],
                        ["extras"] = new JObj { ["model"] = model },
                    });
                    kids.Add((long)(nodes.Count - 1));
                }
            }
            Group(rkey, kids);
        }
        List<object?> pts = J.A(doc["points"]);
        List<object?> splines = J.A(doc["splines"]);
        List<object?> sk = [];
        for (int i = 0; i < splines.Count; i++)
        {
            JObj sp = J.O(splines[i]);
            long end = pts.Count;
            for (int x = i + 1; x < splines.Count; x++)
            {
                if (J.O(splines[x])["points"] is not null)
                {
                    end = J.L(J.O(splines[x])["points"]);
                    break;
                }
            }
            long from = sp["points"] is null ? 0 : J.L(sp["points"]);
            for (long k = from; k < end; k++)
            {
                nodes.Add(new JObj { ["name"] = $"{Nm(sp, i, "spline")}_{k}", ["translation"] = J.O(pts[(int)k])["position"] });
                sk.Add((long)(nodes.Count - 1));
            }
        }
        Group("spline_points", sk);
        return new JObj
        {
            ["asset"] = new JObj { ["version"] = "2.0", ["generator"] = "dwssk bog.py" },
            ["scene"] = 0L,
            ["scenes"] = new List<object?> { new JObj { ["nodes"] = groups } },
            ["nodes"] = nodes,
        };
    }
}
