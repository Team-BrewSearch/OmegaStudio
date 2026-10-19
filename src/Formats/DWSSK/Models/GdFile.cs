using DWSSK.Formats.Core;

namespace DWSSK.Formats.Models;

internal static class GdFile
{
    public static readonly string[] Kinds = ["gdw", "gds", "gdi", "gdt"];
    public static readonly string[] Extensions = [".gdw", ".gds", ".gdi", ".gdt", ".gdt.piece"];

    private static readonly (string Name, string Fmt)[] s_rec =
    [
        ("f00", "I"), ("name1", "I"), ("texture", "H"), ("wrap_s", "B"), ("wrap_t", "B"), ("c0c", "2s"),
        ("f0e", "H"), ("f10", "I"), ("name2", "I"), ("f18", "I"), ("c1c", "H"), ("f1e", "H"), ("value", "4f"),
        ("hash", "I"), ("enabled", "B"), ("f35", "3s"),
    ];

    public static int MaterialSize(Platform p)
    {
        return p switch
        {
            Platform.Wii => 0xAC,
            Platform.N3ds => 0x8C,
            _ => 0xC0,
        };
    }

    public sealed record MatPtrs(int Entries, (int Rec, int Con, int State)[] Passes, (int Start, int End)? Extra);

    public static MatPtrs Ptrs(Platform p)
    {
        return p is Platform.PS3 or Platform.Xbox360
            ? new MatPtrs(2, [(4, 5, 6), (9, 10, 11)], (19, 20))
            : new MatPtrs(2, [(4, 5, 6)], null);
    }

    public static bool IsGd(string file)
    {
        string lower = file.ToLowerInvariant();
        return Extensions.Any(lower.EndsWith);
    }

    public static (JObj Doc, byte[] Bin) Read(byte[] b, Platform platform)
    {
        Reader r = new(b, platform);
        JObj doc = r.Parse();
        Writer w = new(doc, r.Bin.ToArray());
        w.Layout();
        JObj padded = [];
        int o = 0;
        foreach ((string name, ByteWriter data, int align) in w.Blocks)
        {
            o = Bin.Align(o, align);
            int actual = r.Found.TryGetValue(name, out int at) ? at : o;
            if (actual != o)
            {
                if (actual < o || (actual - o) % Math.Min(align, 16) != 0)
                {
                    throw new InvalidDataException($"{name} at 0x{actual:x}, expected 0x{o:x}");
                }
                padded[name] = (long)(actual - o);
                o = actual;
            }
            o += data.Length;
        }
        doc["padded"] = padded;
        doc["end_padding"] = (long)(b.Length - o);
        return (doc, r.Bin.ToArray());
    }

    public static byte[] Write(JObj doc, byte[] bin)
    {
        return new Writer(doc, bin).Build();
    }

    // The material a .gdt draw renders with, from the byte each platform's renderer reads: 0x0E on
    // the Wii (0x801588d0), 0x10 on the PS3 and Xbox 360 (0x8217dd08), 0x00 on the 3DS (0x15293c).
    // The word at 0x00 is unused on the Wii and is the index buffer handle on the PS3 and Xbox 360.
    // Draw data is laid out in this order too.
    internal static long GdtMaterial(Platform plat, JObj d)
    {
        return plat == Platform.N3ds
            ? J.L(d["material"]) & 0xFF
            : plat == Platform.Wii ? (J.L(d["flags"]) >> 8) & 0xFF : J.L(d["flags"]) >> 24;
    }

    private static List<int> StableSort(IEnumerable<int> items, Func<int, long> key)
    {
        return [.. items.Select((v, i) => (v, i)).OrderBy(t => key(t.v)).ThenBy(t => t.i).Select(t => t.v)];
    }

    private static List<int> DataOrder(JObj m)
    {
        if (m.Has("data_order"))
        {
            return [.. J.A(m["data_order"]).Select(J.I)];
        }
        List<object?> draws = J.A(m["draws"]);
        return StableSort(Enumerable.Range(0, draws.Count), d => J.O(draws[d]).Get("created") is { } c ? J.L(c) : d);
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        private readonly Platform _plat;
        private readonly bool _big;
        private List<int> _starts = [];
        private long? _link0;

        public Reader(byte[] b, Platform platform)
        {
            _b = b;
            _plat = platform;
            _big = b.Length > 0 && b[0] == 0;
        }

        public Dictionary<string, int> Found { get; } = [];
        public ByteWriter Bin { get; } = new(1 << 16);

        private uint U(int o)
        {
            return Core.Bin.U32(_b, o, _big);
        }

        private uint[] Us(int o, int n)
        {
            return Core.Bin.U32s(_b, o, n, _big);
        }

        private ushort H(int o)
        {
            return Core.Bin.U16(_b, o, _big);
        }

        private float F(int o)
        {
            return Core.Bin.F32(_b, o, _big);
        }

        private int At(string name, int o)
        {
            Found[name] = o;
            return o;
        }

        private List<object?> Data(string name, int o, int n)
        {
            At(name, o);
            List<object?> reference = [(long)Bin.Length, (long)n];
            Bin.Add(Core.Bin.Slice(_b, o, o + n));
            return reference;
        }

        private string CStr(int o)
        {
            return Core.Bin.CString(_b, o);
        }

        private List<long> NameOffsets(IEnumerable<int> offs)
        {
            List<long> output = [];
            foreach (int o in offs)
            {
                foreach (uint x in new[] { U(o + 4), U(o + 0x14) })
                {
                    if (x != 0)
                    {
                        output.Add(x);
                    }
                }
            }
            return output;
        }

        private static IEnumerable<int> Range(int start, int stop, int step)
        {
            for (int i = start; i < stop; i += step)
            {
                yield return i;
            }
        }

        private JObj Record(int o, JObj names)
        {
            Core.Bin.Need(_b, o, 0x38);
            JObj g = [];
            g["f00"] = (long)U(o);
            g["name1"] = (long)U(o + 4);
            g["texture"] = (long)H(o + 8);
            g["wrap_s"] = (long)_b[o + 10];
            g["wrap_t"] = (long)_b[o + 11];
            byte[] c0c = _b[(o + 12)..(o + 14)];
            g["f0e"] = (long)H(o + 14);
            g["f10"] = (long)U(o + 16);
            g["name2"] = (long)U(o + 20);
            g["f18"] = (long)U(o + 24);
            long c1c = H(o + 28);
            g["f1e"] = (long)H(o + 30);
            g["value"] = J.Doubles(Core.Bin.F32s(_b, o + 32, 4, _big));
            g["hash"] = (long)U(o + 48);
            g["enabled"] = (long)_b[o + 52];
            byte[] f35 = _b[(o + 53)..(o + 56)];
            foreach (string f in new[] { "name1", "name2" })
            {
                if (J.L(g[f]) != 0)
                {
                    string s = CStr((int)J.L(g[f]));
                    g[f] = s;
                    names.SetDefault(s, null);
                }
            }
            JObj r;
            if (c0c[0] == 0xFF && c0c[1] == 0xFF)
            {
                r = new JObj { ["constant"] = Name(g.Pop("name2")), ["register"] = c1c };
            }
            else if (c1c == 0xFFFF)
            {
                r = new JObj { ["transform"] = Name(g.Pop("name1")), ["register"] = (long)H(o + 0x0C) };
            }
            else
            {
                r = new JObj { ["usage"] = (long)c0c[0], ["filter"] = (long)c0c[1] };
                if (c1c != 0)
                {
                    r["c1c"] = c1c;
                }
            }
            g["f35"] = Core.Bin.Hex(f35);
            foreach (KeyValuePair<string, object?> kv in g)
            {
                if (!IsDefault(kv.Value))
                {
                    r[kv.Key] = kv.Value;
                }
            }
            return r;
        }

        private static string? Name(object? v)
        {
            return v is string { Length: > 0 } s ? s : null;
        }

        private static bool IsDefault(object? x)
        {
            return x switch
            {
                long l => l == 0,
                string s => s == "000000",
                List<object?> list => list.Count == 4 && list.All(v => v is double d && d == 0),
                _ => false,
            };
        }

        public JObj Parse()
        {
            uint[] h = Us(0, 20);
            string kind = (h[1] & 0xFF) < Kinds.Length ? Kinds[h[1] & 0xFF] : throw new InvalidDataException($"GD kind {h[1] & 0xFF}");
            JObj doc = new()
            {
                ["platform"] = _plat.Name(),
                ["kind"] = kind,
                ["version"] = (long)h[0],
                ["type"] = (long)(h[1] >> 8),
                ["hash"] = (long)h[16],
            };
            At("header", 0);
            int nm = (int)h[2];
            int om = (int)h[3];
            int nt = (int)h[4];
            int ot = (int)h[5];
            int ns = (int)h[6];
            int os = (int)h[7];
            int msize = MaterialSize(_plat);
            int n = (msize - 0x60) / 4;
            MatPtrs ptrs = Ptrs(_plat);
            List<int> idx = [ptrs.Entries];
            foreach ((int a, int b, int c) in ptrs.Passes)
            {
                idx.AddRange([a, b, c]);
            }
            if (ptrs.Extra is { } extra)
            {
                idx.AddRange([extra.Start, extra.End]);
            }
            SortedSet<int> starts = [ot, os, (int)h[9]];
            for (int i = 0; i < nm; i++)
            {
                uint[] w = Us(om + (i * msize) + 0x60, n);
                foreach (int k in idx)
                {
                    starts.Add((int)w[k]);
                }
            }
            _starts = [.. starts];
            List<object?> materials = [];
            for (int i = 0; i < nm; i++)
            {
                materials.Add(Material(i, om + (i * msize)));
            }
            doc["materials"] = materials;
            At("materials", om);
            At("textures", ot);
            List<object?> textures = [];
            for (int i = 0; i < nt; i++)
            {
                int q = ot + (0x14 * i);
                textures.Add(new List<object?> { (long)U(q), (double)F(q + 4), (double)F(q + 8), (long)U(q + 12), (long)U(q + 16) });
            }
            doc["textures"] = textures;
            At("shaders", os);
            List<object?> shaders = [];
            for (int i = 0; i < ns; i++)
            {
                shaders.Add(J.Longs(Us(os + (0x10 * i), 4)));
            }
            doc["shaders"] = shaders;
            doc["mesh"] = kind switch
            {
                "gdt" => MeshGdt((int)h[9]),
                "gdi" => MeshGdi((int)h[9]),
                "gdw" => MeshGdw((int)h[9]),
                _ => MeshGds((int)h[9]),
            };
            if (h[10..16].Any(x => x != 0))
            {
                doc["skeletons"] = Skeletons(h);
            }
            return doc;
        }

        private JObj Material(int i, int o)
        {
            MatPtrs ptrs = Ptrs(_plat);
            int n = (MaterialSize(_plat) - 0x60) / 4;
            List<object?> w = J.Longs(Us(o + 0x60, n));
            ReadOnlySpan<byte> field = Core.Bin.Slice(_b, o, o + 0x60);
            int end = field.Length;
            while (end > 0 && field[end - 1] == 0)
            {
                end--;
            }
            JObj m = new() { ["name"] = Core.Bin.Latin1(field[..end]) };
            if (Core.Bin.AnyNonZero(field[end..]))
            {
                m["name_bytes"] = Core.Bin.Hex(field[end..]);
            }
            List<object?> passes = [];
            for (int k = 0; k < ptrs.Passes.Length; k++)
            {
                (int pr, int pc, int ps) = ptrs.Passes[k];
                int rec = (int)J.L(w[pr]);
                int con = (int)J.L(w[pc]);
                int st = (int)J.L(w[ps]);
                if (rec == 0)
                {
                    passes.Add(null);
                    continue;
                }
                JObj names = [];
                At($"m{i}p{k}.records", rec);
                JObj p = new() { ["records"] = J.List(Range(rec, con, 0x38).Select(r => (object?)Record(r, names))) };
                List<long> first = NameOffsets(Range(rec, con, 0x38));
                List<object?> consts = [];
                int c = con;
                int nxt = _starts.Where(x => x > con).DefaultIfEmpty(_b.Length).Min();
                nxt = Math.Min(nxt, _b.Length);
                while (c + 0x38 <= nxt
                    && !Core.Bin.AnyNonZero(Core.Bin.Slice(_b, c, c + 4))
                    && Core.Bin.AnyNonZero(Core.Bin.Slice(_b, c, c + 0x38)))
                {
                    first.AddRange(NameOffsets([c]));
                    if (first.Count > 0 && c + 0x38 > first.Min())
                    {
                        break;
                    }
                    consts.Add(Record(c, names));
                    c += 0x38;
                }
                At($"m{i}p{k}.constants", con);
                p["constants"] = consts;
                At($"m{i}p{k}.names", c);
                List<long> offs = NameOffsets(Range(rec, c, 0x38));
                if (offs.Count > 0 && offs.Min() > c)
                {
                    p["names_lead"] = offs.Min() - c;
                }
                if (offs.Count > 0)
                {
                    List<string> table = [];
                    long q = offs.Min();
                    long max = offs.Max();
                    while (q <= max)
                    {
                        table.Add(CStr((int)q));
                        q += table[^1].Length + 1;
                    }
                    if (!table.SequenceEqual(names.Keys))
                    {
                        p["names"] = J.List(table.Select(t => (object?)t));
                    }
                }
                if (st != 0)
                {
                    p["state"] = Core.Bin.Hex(Core.Bin.Slice(_b, At($"m{i}p{k}.state", st), st + 16));
                }
                long sf = J.L(w[ps + 1]);
                p["shader"] = sf & 0xFFFF;
                p["flags"] = sf >> 16;
                passes.Add(p);
                foreach (int x in new[] { pr, pc, ps, ps + 1 })
                {
                    w[x] = null;
                }
            }
            m["passes"] = passes;
            if (ptrs.Extra is { } extra)
            {
                int s = (int)J.L(w[extra.Start]);
                int e = (int)J.L(w[extra.End]);
                m["extra"] = s != 0
                    ? J.List(Range(At($"m{i}.extra", s), e, 0x38).Select(r => (object?)Record(r, [])))
                    : null;
                w[extra.Start] = null;
                w[extra.End] = null;
            }
            int ent = (int)J.L(w[ptrs.Entries]);
            if (ent != 0)
            {
                At($"m{i}.entries", ent);
                List<object?> entries = [];
                int q = ent;
                while (U(q) != 0)
                {
                    entries.Add(new List<object?> { (long)U(q), (long)U(q + 4), (long)U(q + 8), (double)F(q + 12), (long)U(q + 16) });
                    q += 0x14;
                }
                m["entries"] = entries;
            }
            w[ptrs.Entries] = null;
            m["hash"] = w[0];
            w[0] = null;
            m["words"] = w;
            return m;
        }

        private List<object?> Tree(int root)
        {
            HashSet<int> nodes = [];
            List<int> todo = [root];
            while (todo.Count > 0)
            {
                int o = todo[^1];
                todo.RemoveAt(todo.Count - 1);
                if (nodes.Add(o))
                {
                    todo.AddRange(Us(o + 28, 4).Where(x => x != 0).Select(x => (int)x));
                }
            }
            List<int> order = [.. nodes.Order()];
            Dictionary<int, int> index = order.Select((o, k) => (o, k)).ToDictionary(t => t.o, t => t.k);
            List<object?> output = [];
            foreach (int o in order)
            {
                uint[] v = Us(o + 24, 5);
                JObj n = new()
                {
                    ["min"] = J.Doubles(Core.Bin.F32s(_b, o, 3, _big)),
                    ["max"] = J.Doubles(Core.Bin.F32s(_b, o + 12, 3, _big)),
                    ["children"] = J.List(v[1..].Select(x => index.TryGetValue((int)x, out int k) ? (object?)(long)k : null)),
                };
                if (v[0] != 0)
                {
                    n["word"] = (long)v[0];
                }
                output.Add(n);
            }
            return output;
        }

        private JObj MeshGdt(int o)
        {
            At("mesh", o);
            uint fmt = U(o);
            int nl = (int)U(o + 4);
            List<object?> lods = [];
            bool small = _plat is Platform.Wii or Platform.N3ds;
            int dsize = _plat == Platform.Wii ? 0x10 : 0x14;
            int stride = _plat is Platform.PS3 or Platform.Xbox360 ? 72 : 24;
            string key = _plat == Platform.Wii ? "dl" : "indices";
            for (int l = 0; l < nl; l++)
            {
                int q = o + 8 + (0x30 * l);
                double dist = F(q);
                double scale = F(q + 4);
                uint[] lists = Us(q + 8, 5);
                uint[] t = Us(q + 28, 5);
                (uint vc, uint vo, List<object?> extra) = small
                    ? (t[2], t[3], J.Longs(new[] { t[0], t[1], t[4] }))
                    : (t[0], t[1], J.Longs(new[] { t[2], t[3], t[4] }));
                List<object?>? draws = [];
                foreach (int d in Range((int)lists[0], (int)lists[4], dsize))
                {
                    if (_plat == Platform.Wii)
                    {
                        uint[] v = Us(d, 4);
                        draws.Add(new JObj
                        {
                            ["material"] = (long)v[0],
                            ["flags"] = (long)v[3],
                            ["display_list"] = Data($"l{l}.d{draws.Count}.dl", (int)v[1], (int)v[2]),
                        });
                    }
                    else
                    {
                        uint[] v = Us(d, 5);
                        draws.Add(new JObj
                        {
                            ["material"] = (long)v[0],
                            ["primitive"] = (long)v[3],
                            ["flags"] = (long)v[4],
                            ["indices"] = Data($"l{l}.d{draws.Count}.indices", (int)v[2], 2 * (int)v[1]),
                        });
                    }
                }
                if (lists[0] != 0)
                {
                    At($"l{l}.draws", (int)lists[0]);
                }
                else
                {
                    draws = null;
                }
                int count = draws?.Count ?? 0;
                List<int> order = StableSort(Enumerable.Range(0, count), j => Found[$"l{l}.d{j}.{key}"]);
                JObj lod = new()
                {
                    ["distance"] = dist,
                    ["scale"] = scale,
                    ["words"] = extra,
                    ["lists"] = J.Longs(Enumerable.Range(0, 4).Select(k => (long)Core.Bin.FloorDiv((int)lists[k + 1] - (int)lists[k], dsize))),
                    ["draws"] = draws,
                    ["vertices"] = Data($"l{l}.vertices", (int)vo, stride * (int)vc),
                };
                lods.Add(lod);
                if (draws is { Count: > 0 })
                {
                    List<int> sorted = StableSort(order, j => GdtMaterial(_plat, J.O(draws[j])));
                    if (!order.SequenceEqual(sorted))
                    {
                        lod["data_order"] = J.Longs(order);
                    }
                }
            }
            return new JObj { ["format"] = (long)fmt, ["lods"] = lods, ["tail"] = J.Longs(Us(o + 0x128, 4)) };
        }

        private JObj MeshGdi(int o)
        {
            At("mesh", o);
            uint[] w = Us(o, 16);
            int stride = _plat == Platform.Wii ? 36 : 64;
            List<object?> words = J.Longs(new[] { w[1], w[8] }.Concat(w[11..]));
            JObj mesh = new()
            {
                ["format"] = (long)w[0],
                ["sphere"] = J.Doubles(Core.Bin.F32s(_b, o + 16, 4, _big)),
                ["words"] = words,
            };
            mesh["vertices"] = Data("vertices", (int)w[9], stride * (int)w[2]);
            mesh["indices"] = Data("indices", (int)w[10], 2 * (int)w[3]);
            return mesh;
        }

        private JObj MeshGdw(int o)
        {
            At("mesh", o);
            uint[] w = Us(o, 8);
            bool wii = _plat == Platform.Wii;
            int stride = _plat is Platform.PS3 or Platform.Xbox360 ? 72 : 24;
            JObj mesh = new()
            {
                ["format"] = (long)w[0],
                ["scale"] = J.Doubles(Core.Bin.F32s(_b, o + 4, 2, _big)),
                ["words"] = J.Longs(new[] { w[3], w[7] }),
            };
            mesh["tree"] = Tree(At("tree", (int)w[6]));
            List<object?> batches = [];
            mesh["batches"] = batches;
            At("batches", (int)w[5]);
            for (int i = 0; i < w[4]; i++)
            {
                int bo = (int)w[5] + (8 * i);
                ushort mat = H(bo);
                ushort nsub = H(bo + 2);
                int sp = (int)U(bo + 4);
                List<object?>? subs = sp != 0 ? [] : null;
                JObj batch = new() { ["material"] = (long)mat, ["subs"] = subs };
                if (sp != 0)
                {
                    At($"b{i}.subs", sp);
                }
                for (int j = 0; j < nsub; j++)
                {
                    int so = sp + (16 * j);
                    ushort zone = H(so);
                    ushort nd = H(so + 2);
                    int dp = (int)U(so + 4);
                    uint x = U(so + 8);
                    uint y = U(so + 12);
                    if (dp != 0)
                    {
                        At($"b{i}.s{j}.draws", dp);
                    }
                    List<object?> draws = [];
                    for (int k = 0; k < nd; k++)
                    {
                        int q = dp + ((wii ? 0x2C : 0x3C) * k);
                        string n = $"b{i}.s{j}.d{k}";
                        JObj d;
                        int vc;
                        int vo;
                        if (wii)
                        {
                            List<object?> sph = J.Doubles(Core.Bin.F32s(_b, q, 4, _big));
                            uint dl = U(q + 16);
                            uint size = U(q + 20);
                            ushort a = H(q + 24);
                            vc = H(q + 26);
                            ushort e = H(q + 28);
                            ushort fl = H(q + 30);
                            vo = (int)U(q + 32);
                            d = new JObj
                            {
                                ["sphere"] = sph,
                                ["leaf"] = (long)a,
                                ["e"] = (long)e,
                                ["flags"] = (long)fl,
                                ["words"] = J.Longs(new[] { U(q + 36), U(q + 40) }),
                                ["display_list"] = Data(n + ".dl", (int)dl, (int)size),
                            };
                        }
                        else
                        {
                            ushort a = H(q);
                            ushort ic = H(q + 2);
                            uint ip = U(q + 4);
                            vc = H(q + 8);
                            ushort e = H(q + 10);
                            ushort fl = H(q + 12);
                            ushort b2 = H(q + 14);
                            vo = (int)U(q + 16);
                            List<object?> words = J.Longs(new[] { U(q + 20), U(q + 24) });
                            words.AddRange(J.Longs(Us(q + 44, 4)));
                            d = new JObj
                            {
                                ["sphere"] = J.Doubles(Core.Bin.F32s(_b, q + 28, 4, _big)),
                                ["leaf"] = (long)a,
                                ["e"] = (long)e,
                                ["flags"] = (long)fl,
                                ["b"] = (long)b2,
                                ["words"] = words,
                                ["indices"] = Data(n + ".indices", (int)ip, 2 * ic),
                            };
                        }
                        d["vertices"] = Data(n + ".vertices", vo, stride * vc);
                        draws.Add(d);
                    }
                    (subs ?? throw new InvalidDataException($"batch {i} has sub tables but no pointer to them")).Add(new JObj
                    {
                        ["zone"] = (long)zone,
                        ["words"] = J.Longs(new[] { x, y }),
                        ["draws"] = dp != 0 ? draws : null,
                    });
                }
                batches.Add(batch);
            }
            return mesh;
        }

        private JObj MeshGds(int o)
        {
            At("mesh", o);
            uint fmt = U(o);
            uint flags = U(o + 4);
            int ns = (int)U(o + 8);
            int sub = _plat switch
            {
                Platform.PS3 => 0x74,
                Platform.Wii => 0x28,
                _ => 0x14,
            };
            List<object?> submeshes = [];
            JObj mesh = new()
            {
                ["format"] = (long)fmt,
                ["flags"] = (long)flags,
                ["submeshes"] = submeshes,
                ["word"] = (long)U(o + 12 + (6 * sub)),
            };
            _link0 = U(o + 16 + (6 * sub));
            for (int k = 0; k < ns; k++)
            {
                int q = o + 12 + (sub * k);
                string n = $"s{k}";
                if (_plat == Platform.Wii)
                {
                    uint[] v = Us(q, 10);
                    List<object?> wdraws = [];
                    for (int d = 0; d < v[0]; d++)
                    {
                        int r = (int)v[1] + (12 * d);
                        uint dl = U(r);
                        uint size = U(r + 4);
                        wdraws.Add(new JObj
                        {
                            ["material"] = (long)_b[r + 8],
                            ["created"] = (long)_b[r + 9],
                            ["mask"] = (long)_b[r + 10],
                            ["bone"] = (long)_b[r + 11],
                            ["display_list"] = Data($"{n}.d{d}.di", (int)dl, (int)size),
                        });
                    }
                    At($"{n}.draws", (int)v[1]);
                    JObj wm = new() { ["draws"] = wdraws };
                    string[] arrays = ["skinned", "rigid", "uv", "colour"];
                    int[] sizes = [32, 10, 4, 4];
                    for (int a = 0; a < 4; a++)
                    {
                        uint cnt = v[2 + (2 * a)];
                        uint ptr = v[3 + (2 * a)];
                        wm[arrays[a]] = ptr != 0 ? Data($"{n}.{arrays[a]}", (int)ptr, sizes[a] * (int)cnt) : null;
                    }
                    submeshes.Add(wm);
                    continue;
                }
                uint[] lists = Us(q, 5);
                int dsize = _plat switch
                {
                    Platform.PS3 => 0x10,
                    Platform.Xbox360 => 0x50,
                    _ => 0x40,
                };
                List<object?> draws = [];
                JObj m = new()
                {
                    ["lists"] = J.Longs(Enumerable.Range(0, 4).Select(g => (long)Core.Bin.FloorDiv((int)lists[g + 1] - (int)lists[g], dsize))),
                    ["draws"] = draws,
                };
                At($"{n}.draws", (int)lists[0]);
                if (_plat == Platform.PS3)
                {
                    List<object?> groups = [];
                    m["groups"] = groups;
                    for (int g = 0; g < 4; g++)
                    {
                        uint[] v = Us(q + 20 + (24 * g), 6);
                        JObj grp = new();
                        grp["skinned"] = v[2] != 0 ? Data($"{n}.g{g}.skinned", (int)v[2], 80 * (int)v[0]) : null;
                        grp["rigid"] = v[3] != 0 ? Data($"{n}.g{g}.rigid", (int)v[3], 64 * (int)v[1]) : null;
                        grp["words"] = J.Longs(new[] { v[4], v[5] });
                        groups.Add(grp);
                    }
                }
                int dIndex = 0;
                foreach (int r in Range((int)lists[0], (int)lists[4], dsize))
                {
                    string dn = $"{n}.d{dIndex}";
                    if (_plat == Platform.PS3)
                    {
                        uint[] v = Us(r, 4);
                        draws.Add(new JObj
                        {
                            ["word"] = (long)v[2],
                            ["flags"] = (long)v[3],
                            ["indices"] = Data(dn + ".indices", (int)v[1], 2 * (int)v[0]),
                        });
                    }
                    else if (_plat == Platform.Xbox360)
                    {
                        uint[] v = Us(r, 6);
                        int palLen = _b[r + 0x18];
                        ReadOnlySpan<byte> pal = Core.Bin.Slice(_b, r + 0x19, r + 0x19 + palLen);
                        int stride = (_b[r + 0x4B] & 0x10) != 0 ? 64 : 72;
                        JObj d = new()
                        {
                            ["words"] = J.Longs(new[] { v[2], v[5] }),
                            ["palette"] = J.Longs(pal),
                            ["palette_rest"] = Core.Bin.Hex(Core.Bin.Slice(_b, r + 0x19 + pal.Length, r + 0x49)),
                            ["material"] = (long)_b[r + 0x49],
                            ["created"] = (long)_b[r + 0x4A],
                            ["mask"] = (long)_b[r + 0x4B],
                            ["bone"] = (long)_b[r + 0x4C],
                            ["tail"] = Core.Bin.Hex(Core.Bin.Slice(_b, r + 0x4D, r + 0x50)),
                        };
                        d["indices"] = Data(dn + ".indices", (int)v[1], 2 * (int)v[0]);
                        d["vertices"] = Data(dn + ".vertices", (int)v[4], stride * (int)v[3]);
                        draws.Add(d);
                    }
                    else
                    {
                        uint[] v = Us(r, 8);
                        int palLen = _b[r + 0x20];
                        ReadOnlySpan<byte> pal = Core.Bin.Slice(_b, r + 0x21, r + 0x21 + palLen);
                        JObj d = new()
                        {
                            ["words"] = J.Longs(new[] { v[2], v[7] }),
                            ["palette"] = J.Longs(pal),
                            ["palette_rest"] = Core.Bin.Hex(Core.Bin.Slice(_b, r + 0x21 + pal.Length, r + 0x39)),
                            ["material"] = (long)_b[r + 0x39],
                            ["created"] = (long)_b[r + 0x3A],
                            ["b3b"] = (long)_b[r + 0x3B],
                            ["mask"] = (long)_b[r + 0x3C],
                            ["b3d"] = (long)_b[r + 0x3D],
                            ["bone"] = (long)_b[r + 0x3E],
                            ["b3f"] = (long)_b[r + 0x3F],
                        };
                        d["indices"] = Data(dn + ".indices", (int)v[1], 2 * (int)v[0]);
                        d["skinned"] = Data(dn + ".skinned", (int)v[4], 32 * (int)v[3]);
                        d["rigid"] = Data(dn + ".rigid", (int)v[6], 24 * (int)v[5]);
                        draws.Add(d);
                    }
                    dIndex++;
                }
                submeshes.Add(m);
            }
            for (int k = 0; k < submeshes.Count; k++)
            {
                JObj m = J.O(submeshes[k]);
                string key = _plat == Platform.Wii ? "di" : "indices";
                int count = J.A(m["draws"]).Count;
                List<int> order = StableSort(Enumerable.Range(0, count), d => Found[$"s{k}.d{d}.{key}"]);
                if (!order.SequenceEqual(DataOrder(m)))
                {
                    m["data_order"] = J.Longs(order);
                }
            }
            return mesh;
        }

        private List<object?> Skeletons(uint[] h)
        {
            List<object?> output = [];
            for (int k = 0; k < 6; k++)
            {
                int o = (int)h[10 + k];
                if (o == 0)
                {
                    output.Add(null);
                    continue;
                }
                uint[] v = Us(o, 8);
                int n = (int)v[1];
                int bo = (int)v[2];
                int po = (int)v[3];
                long link = v[5] != 0 ? v[5] : k == 0 ? _link0 ?? throw new InvalidDataException("the first skeleton's link block is named by a mesh header this model does not have") : 0;
                At($"k{k}.header", o);
                int size = k != 0 ? n : 16;
                JObj sk = new()
                {
                    ["link"] = link != 0 ? Core.Bin.Hex(Core.Bin.Slice(_b, At($"k{k}.link", (int)link), (int)link + size)) : null,
                    ["words"] = J.Longs(new[] { v[4], v[6], v[7] }),
                };
                List<object?> bones = [];
                List<object?> poses = [];
                sk["bones"] = bones;
                sk["poses"] = poses;
                At($"k{k}.bones", bo);
                At($"k{k}.poses", po);
                for (int i = 0; i < n; i++)
                {
                    int bb = bo + (0x50 * i);
                    bones.Add(new JObj
                    {
                        ["matrix"] = J.Doubles(Core.Bin.F32s(_b, bb, 16, _big)),
                        ["hash"] = (long)U(bb + 0x40),
                        ["word"] = (long)U(bb + 0x44),
                        ["parent"] = (long)Core.Bin.U8(_b, bb + 0x48),
                        ["rest"] = Core.Bin.Hex(Core.Bin.Slice(_b, bb + 0x49, bo + (0x50 * (i + 1)))),
                    });
                    poses.Add(J.Doubles(Core.Bin.F32s(_b, po + (0x30 * i), 12, _big)));
                }
                output.Add(sk);
            }
            return output;
        }
    }

    private sealed class Writer
    {
        private readonly JObj _doc;
        private readonly byte[] _bin;
        private readonly bool _big;
        private readonly Platform _plat;
        private readonly List<(string Block, int Pos, string Target, int Add)> _fix = [];
        private readonly List<(string Block, int Pos, string Target)> _endFix = [];
        private (string Block, Dictionary<string, int> Names) _names;

        public Writer(JObj doc, byte[] bin)
        {
            _doc = doc;
            _bin = bin;
            _plat = Platforms.Parse(J.S(doc["platform"]));
            _big = _plat != Platform.N3ds;
        }

        public List<(string Name, ByteWriter Data, int Align)> Blocks { get; } = [];

        private ByteWriter Block(string name, ReadOnlySpan<byte> data = default, int align = 16)
        {
            ByteWriter w = new(Math.Max(16, data.Length));
            w.Add(data);
            Blocks.Add((name, w, align));
            return w;
        }

        private void Ptr(string block, int pos, string? target)
        {
            if (target is not null)
            {
                _fix.Add((block, pos, target, 0));
            }
        }

        private ReadOnlySpan<byte> Raw(object? reference)
        {
            List<object?> r = J.A(reference);
            int at = J.I(r[0]);
            int n = J.I(r[1]);
            return Core.Bin.Slice(_bin, at, at + n);
        }

        private static int RefSize(object? reference)
        {
            return J.I(J.A(reference)[1]);
        }

        private void U32(ByteWriter w, object? v)
        {
            w.U32(J.U(v), _big);
        }

        private void U16(ByteWriter w, object? v)
        {
            w.U16(J.U16(v), _big);
        }

        private void F32(ByteWriter w, object? v)
        {
            w.F32(J.D(v), _big);
        }

        private byte[] Record(JObj r)
        {
            JObj g = r.Copy();
            byte[] c0c;
            if (g.Has("constant"))
            {
                g["name2"] = g.Pop("constant");
                g["c1c"] = g.Pop("register");
                c0c = [0xFF, 0xFF];
            }
            else if (g.Has("transform"))
            {
                g["name1"] = g.Pop("transform");
                byte[] reg = new byte[2];
                Core.Bin.PutU16(reg, 0, J.U16(g.Pop("register")), _big);
                c0c = reg;
                g["c1c"] = 0xFFFFL;
            }
            else
            {
                c0c = [J.U8(g.Get("usage", 0L)), J.U8(g.Get("filter", 0L))];
                g.Remove("usage");
                g.Remove("filter");
            }
            ByteWriter o = new(0x38);
            U32(o, g.Get("f00", 0L));
            o.U32(0, _big);
            U16(o, g.Get("texture", 0L));
            o.Add(J.U8(g.Get("wrap_s", 0L)));
            o.Add(J.U8(g.Get("wrap_t", 0L)));
            o.Add(c0c);
            U16(o, g.Get("f0e", 0L));
            U32(o, g.Get("f10", 0L));
            o.U32(0, _big);
            U32(o, g.Get("f18", 0L));
            U16(o, g.Get("c1c", 0L));
            U16(o, g.Get("f1e", 0L));
            List<object?> value = g.Get("value") is List<object?> vl ? vl : [0.0, 0.0, 0.0, 0.0];
            if (value.Count != 4)
            {
                throw new StructException("a record value has 4 floats");
            }
            foreach (object? x in value)
            {
                F32(o, x);
            }
            U32(o, g.Get("hash", 0L));
            o.Add(J.U8(g.Get("enabled", 0L)));
            byte[] f35 = Core.Bin.FromHex(J.S(g.Get("f35", "000000")));
            o.Add(f35.AsSpan(0, Math.Min(3, f35.Length)));
            o.Zeros(3 - Math.Min(3, f35.Length));
            return o.ToArray();
        }

        private static (string? Name1, string? Name2) NamesOf(JObj r)
        {
            if (r.Has("constant"))
            {
                return (r.Get("name1") as string, r["constant"] as string);
            }
            if (r.Has("transform"))
            {
                return (r["transform"] as string, r.Get("name2") as string);
            }
            return (r.Get("name1") as string, r.Get("name2") as string);
        }

        private void NameFix(string block, int pos, JObj r)
        {
            (string nb, Dictionary<string, int> names) = _names;
            (string? a, string? b) = NamesOf(r);
            if (!string.IsNullOrEmpty(a))
            {
                _fix.Add((block, pos + 4, nb, names[a]));
            }
            if (!string.IsNullOrEmpty(b))
            {
                _fix.Add((block, pos + 0x14, nb, names[b]));
            }
        }

        public void Layout()
        {
            JObj d = _doc;
            ByteWriter hdr = Block("header", new byte[0x50]);
            ByteWriter mats = Block("materials");
            List<object?> materials = J.A(d["materials"]);
            for (int i = 0; i < materials.Count; i++)
            {
                mats.Add(Material(i, J.O(materials[i]), mats.Length));
            }
            ByteWriter tex = new();
            foreach (object? t in J.A(d["textures"]))
            {
                List<object?> v = J.A(t);
                U32(tex, v[0]);
                F32(tex, v[1]);
                F32(tex, v[2]);
                U32(tex, v[3]);
                U32(tex, v[4]);
            }
            Block("textures", tex.Span);
            ByteWriter sh = new();
            foreach (object? t in J.A(d["shaders"]))
            {
                foreach (object? x in J.A(t))
                {
                    U32(sh, x);
                }
            }
            sh.Zeros(16);
            Block("shaders", sh.Span);
            string kind = J.S(d["kind"]);
            JObj mesh = J.O(d["mesh"]);
            switch (kind)
            {
                case "gdt":
                    MeshGdt(mesh);
                    break;
                case "gdi":
                    MeshGdi(mesh);
                    break;
                case "gdw":
                    MeshGdw(mesh);
                    break;
                default:
                    MeshGds(mesh);
                    break;
            }
            if (d.Get("skeletons") is List<object?> { Count: > 0 } sks)
            {
                Skeletons(sks);
            }
            hdr.SetLength(0);
            uint[] h =
            [
                J.U(d["version"]), (J.U(d["type"]) << 8) | (uint)Array.IndexOf(Kinds, kind), (uint)materials.Count, 0,
                (uint)J.A(d["textures"]).Count, 0, (uint)J.A(d["shaders"]).Count, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                J.U(d["hash"]), 0, 0, 0,
            ];
            foreach (uint x in h)
            {
                hdr.U32(x, _big);
            }
            Ptr("header", 0x0C, "materials");
            Ptr("header", 0x14, "textures");
            Ptr("header", 0x1C, "shaders");
            Ptr("header", 0x24, "mesh");
        }

        private byte[] Material(int i, JObj m, int at)
        {
            MatPtrs ptrs = Ptrs(_plat);
            List<object?> w = [.. J.A(m["words"])];
            w[0] = m["hash"];
            List<object?> passes = J.A(m["passes"]);
            for (int k = 0; k < passes.Count; k++)
            {
                (int pr, int pc, int ps) = ptrs.Passes[k];
                if (passes[k] is not JObj pas)
                {
                    w[pr] = 0L;
                    w[pc] = 0L;
                    w[ps] = 0L;
                    continue;
                }
                w[ps + 1] = (J.L(pas["flags"]) << 16) | J.L(pas["shader"]);
                Dictionary<string, int> names = new(StringComparer.Ordinal);
                ByteWriter nb = new();
                nb.Zeros((int)J.L(pas.Get("names_lead", 0L)));
                List<object?> recs = J.A(pas["records"]);
                List<object?> cons = J.A(pas["constants"]);
                IEnumerable<string> order = pas.Get("names") is List<object?> { Count: > 0 } explicitNames
                    ? explicitNames.Select(J.S)
                    : recs.Concat(cons).SelectMany(r =>
                    {
                        (string? a, string? b) = NamesOf(J.O(r));
                        return new[] { a, b }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!);
                    });
                foreach (string x in order)
                {
                    if (!names.ContainsKey(x))
                    {
                        names[x] = nb.Length;
                        nb.Add(Core.Bin.Latin1(x));
                        nb.Add(0);
                    }
                }
                string bse = $"m{i}p{k}";
                ByteWriter recBlock = Block(bse + ".records");
                ByteWriter conBlock = Block(bse + ".constants", default, 1);
                Block(bse + ".names", nb.Span, 1);
                _names = (bse + ".names", names);
                for (int j = 0; j < recs.Count; j++)
                {
                    recBlock.Add(Record(J.O(recs[j])));
                    NameFix(bse + ".records", j * 0x38, J.O(recs[j]));
                }
                for (int j = 0; j < cons.Count; j++)
                {
                    conBlock.Add(Record(J.O(cons[j])));
                    NameFix(bse + ".constants", j * 0x38, J.O(cons[j]));
                }
                foreach ((int x, string t) in new[] { (pr, ".records"), (pc, ".constants") })
                {
                    w[x] = 0L;
                    Ptr("materials", at + 0x60 + (4 * x), bse + t);
                }
                w[ps] = 0L;
                if (pas.Has("state"))
                {
                    Block(bse + ".state", Core.Bin.FromHex(J.S(pas["state"])));
                    Ptr("materials", at + 0x60 + (4 * ps), bse + ".state");
                }
            }
            if (ptrs.Extra is { } extra)
            {
                w[extra.Start] = 0L;
                w[extra.End] = 0L;
                if (m["extra"] is List<object?> recs)
                {
                    ByteWriter eb = new();
                    foreach (object? r in recs)
                    {
                        eb.Add(Record(J.O(r)));
                    }
                    Block($"m{i}.extra", eb.Span);
                    Ptr("materials", at + 0x60 + (4 * extra.Start), $"m{i}.extra");
                    _endFix.Add(("materials", at + 0x60 + (4 * extra.End), $"m{i}.extra"));
                }
            }
            w[ptrs.Entries] = 0L;
            if (m.Has("entries"))
            {
                ByteWriter eb = new();
                foreach (object? e in J.A(m["entries"]))
                {
                    List<object?> v = J.A(e);
                    U32(eb, v[0]);
                    U32(eb, v[1]);
                    U32(eb, v[2]);
                    F32(eb, v[3]);
                    U32(eb, v[4]);
                }
                Block($"m{i}.entries", eb.Span);
                Ptr("materials", at + 0x60 + (4 * ptrs.Entries), $"m{i}.entries");
            }
            ByteWriter o = new(MaterialSize(_plat));
            byte[] name = Core.Bin.Latin1(J.S(m["name"]));
            o.Add(name);
            if (m.Has("name_bytes"))
            {
                o.Add(Core.Bin.FromHex(J.S(m["name_bytes"])));
            }
            else
            {
                if (name.Length > 0x60)
                {
                    throw new StructException($"material name \"{J.S(m["name"])}\" is longer than 96 bytes");
                }
                o.Zeros(0x60 - name.Length);
            }
            foreach (object? x in w)
            {
                U32(o, x);
            }
            return o.ToArray();
        }

        private void MeshGdi(JObj mesh)
        {
            int stride = _plat == Platform.Wii ? 36 : 64;
            List<object?> w = J.A(mesh["words"]);
            ByteWriter blk = new();
            U32(blk, mesh["format"]);
            U32(blk, w[0]);
            blk.U32((uint)(RefSize(mesh["vertices"]) / stride), _big);
            blk.U32((uint)(RefSize(mesh["indices"]) / 2), _big);
            foreach (object? s in J.A(mesh["sphere"]))
            {
                F32(blk, s);
            }
            U32(blk, w[1]);
            blk.U32(0, _big);
            blk.U32(0, _big);
            if (w.Count != 7)
            {
                throw new StructException("an instanced mesh has 7 words");
            }
            foreach (object? x in w.Skip(2))
            {
                U32(blk, x);
            }
            Block("mesh", blk.Span);
            Ptr("mesh", 0x24, "vertices");
            Ptr("mesh", 0x28, "indices");
            Block("vertices", Raw(mesh["vertices"]));
            Block("indices", Raw(mesh["indices"]));
        }

        private void MeshGdw(JObj mesh)
        {
            bool wii = _plat == Platform.Wii;
            int stride = _plat is Platform.PS3 or Platform.Xbox360 ? 72 : 24;
            List<object?> w = J.A(mesh["words"]);
            List<object?> batches = J.A(mesh["batches"]);
            ByteWriter head = new();
            U32(head, mesh["format"]);
            foreach (object? s in J.A(mesh["scale"]))
            {
                F32(head, s);
            }
            U32(head, w[0]);
            head.U32((uint)batches.Count, _big);
            head.U32(0, _big);
            head.U32(0, _big);
            foreach (object? x in w.Skip(1))
            {
                U32(head, x);
            }
            Block("mesh", head.Span);
            Ptr("mesh", 0x14, "batches");
            Ptr("mesh", 0x18, "tree");
            ByteWriter bt = Block("batches");
            for (int i = 0; i < batches.Count; i++)
            {
                JObj batch = J.O(batches[i]);
                List<object?>? subs = batch["subs"] as List<object?>;
                U16(bt, batch["material"]);
                bt.U16((uint)(subs?.Count ?? 0), _big);
                bt.U32(0, _big);
                if (subs is null)
                {
                    continue;
                }
                Ptr("batches", (8 * i) + 4, $"b{i}.subs");
                ByteWriter sb = Block($"b{i}.subs");
                for (int j = 0; j < subs.Count; j++)
                {
                    JObj sub = J.O(subs[j]);
                    List<object?>? draws = sub["draws"] as List<object?>;
                    U16(sb, sub["zone"]);
                    sb.U16((uint)(draws?.Count ?? 0), _big);
                    sb.U32(0, _big);
                    foreach (object? x in J.A(sub["words"]))
                    {
                        U32(sb, x);
                    }
                    if (draws is null)
                    {
                        continue;
                    }
                    string dname = $"b{i}.s{j}.draws";
                    Ptr($"b{i}.subs", (16 * j) + 4, dname);
                    ByteWriter db = Block(dname);
                    int size = wii ? 0x2C : 0x3C;
                    for (int k = 0; k < draws.Count; k++)
                    {
                        JObj d = J.O(draws[k]);
                        string n = $"b{i}.s{j}.d{k}";
                        int vc = RefSize(d["vertices"]) / stride;
                        List<object?> dw = J.A(d["words"]);
                        if (wii)
                        {
                            foreach (object? s in J.A(d["sphere"]))
                            {
                                F32(db, s);
                            }
                            db.U32(0, _big);
                            db.U32((uint)RefSize(d["display_list"]), _big);
                            U16(db, d["leaf"]);
                            db.U16((uint)vc, _big);
                            U16(db, d["e"]);
                            U16(db, d["flags"]);
                            db.U32(0, _big);
                            U32(db, dw[0]);
                            U32(db, dw[1]);
                            Ptr(dname, (k * size) + 16, n + ".dl");
                            Ptr(dname, (k * size) + 32, n + ".vertices");
                        }
                        else
                        {
                            U16(db, d["leaf"]);
                            db.U16((uint)(RefSize(d["indices"]) / 2), _big);
                            db.U32(0, _big);
                            db.U16((uint)vc, _big);
                            U16(db, d["e"]);
                            U16(db, d["flags"]);
                            U16(db, d["b"]);
                            db.U32(0, _big);
                            U32(db, dw[0]);
                            U32(db, dw[1]);
                            foreach (object? s in J.A(d["sphere"]))
                            {
                                F32(db, s);
                            }
                            foreach (object? x in dw.Skip(2))
                            {
                                U32(db, x);
                            }
                            Ptr(dname, (k * size) + 4, n + ".indices");
                            Ptr(dname, (k * size) + 16, n + ".vertices");
                        }
                    }
                    for (int k = 0; k < draws.Count; k++)
                    {
                        JObj d = J.O(draws[k]);
                        string n = $"b{i}.s{j}.d{k}";
                        Block(n + (wii ? ".dl" : ".indices"), Raw(d[wii ? "display_list" : "indices"]));
                        Block(n + ".vertices", Raw(d["vertices"]));
                    }
                }
            }
            ByteWriter tree = Block("tree", default, 4);
            List<object?> nodes = J.A(mesh["tree"]);
            for (int k = 0; k < nodes.Count; k++)
            {
                JObj n = J.O(nodes[k]);
                foreach (object? v in J.A(n["min"]).Concat(J.A(n["max"])))
                {
                    F32(tree, v);
                }
                U32(tree, n.Get("word", 0L));
                tree.Zeros(16);
                List<object?> children = J.A(n["children"]);
                for (int c = 0; c < children.Count; c++)
                {
                    if (children[c] is not null)
                    {
                        _fix.Add(("tree", (44 * k) + 28 + (4 * c), "tree", 44 * J.I(children[c])));
                    }
                }
            }
        }

        private void MeshGds(JObj mesh)
        {
            int sub = _plat switch
            {
                Platform.PS3 => 0x74,
                Platform.Wii => 0x28,
                _ => 0x14,
            };
            List<object?> submeshes = J.A(mesh["submeshes"]);
            ByteWriter hdr = Block("mesh", new byte[20 + (6 * sub)]);
            hdr.PatchU32(0, J.U(mesh["format"]), _big);
            hdr.PatchU32(4, J.U(mesh["flags"]), _big);
            hdr.PatchU32(8, (uint)submeshes.Count, _big);
            hdr.PatchU32(12 + (6 * sub), J.U(mesh["word"]), _big);
            List<object?> sk = _doc.Get("skeletons") as List<object?> ?? [];
            if (sk.Count > 0 && sk[0] is JObj first && first["link"] is string { Length: > 0 })
            {
                Ptr("mesh", 16 + (6 * sub), "k0.link");
            }
            for (int k = 0; k < submeshes.Count; k++)
            {
                JObj m = J.O(submeshes[k]);
                int q = 12 + (sub * k);
                string n = $"s{k}";
                List<int> order = DataOrder(m);
                List<object?> draws = J.A(m["draws"]);
                if (_plat == Platform.Wii)
                {
                    hdr.PatchU32(q, (uint)draws.Count, true);
                    Ptr("mesh", q + 4, $"{n}.draws");
                    ByteWriter db = Block($"{n}.draws");
                    for (int d = 0; d < draws.Count; d++)
                    {
                        JObj x = J.O(draws[d]);
                        db.U32(0, _big);
                        db.U32((uint)RefSize(x["display_list"]), _big);
                        db.Add(J.U8(x["material"]));
                        db.Add(J.U8(x["created"]));
                        db.Add(J.U8(x["mask"]));
                        db.Add(J.U8(x["bone"]));
                        Ptr($"{n}.draws", 12 * d, $"{n}.d{d}.di");
                    }
                    foreach (int d in order)
                    {
                        Block($"{n}.d{d}.di", Raw(J.O(draws[d])["display_list"]));
                    }
                    string[] arrays = ["skinned", "rigid", "uv", "colour"];
                    int[] sizes = [32, 10, 4, 4];
                    for (int j = 0; j < 4; j++)
                    {
                        if (m[arrays[j]] is not null)
                        {
                            hdr.PatchU32(q + 8 + (8 * j), (uint)(RefSize(m[arrays[j]]) / sizes[j]), true);
                            Ptr("mesh", q + 12 + (8 * j), $"{n}.{arrays[j]}");
                            Block($"{n}.{arrays[j]}", Raw(m[arrays[j]]));
                        }
                    }
                    continue;
                }
                int dsize = _plat switch
                {
                    Platform.PS3 => 0x10,
                    Platform.Xbox360 => 0x50,
                    _ => 0x40,
                };
                List<object?> lists = J.A(m["lists"]);
                int c = 0;
                for (int g = 0; g < 5; g++)
                {
                    _fix.Add(("mesh", q + (4 * g), $"{n}.draws", c * dsize));
                    c += g < 4 ? J.I(lists[g]) : 0;
                }
                ByteWriter dbk = Block($"{n}.draws");
                for (int d = 0; d < draws.Count; d++)
                {
                    JObj x = J.O(draws[d]);
                    string dn = $"{n}.d{d}";
                    if (_plat == Platform.PS3)
                    {
                        dbk.U32((uint)(RefSize(x["indices"]) / 2), _big);
                        dbk.U32(0, _big);
                        U32(dbk, x["word"]);
                        U32(dbk, x["flags"]);
                        Ptr($"{n}.draws", (16 * d) + 4, dn + ".indices");
                    }
                    else if (_plat == Platform.Xbox360)
                    {
                        int stride = (J.L(x["mask"]) & 0x10) != 0 ? 64 : 72;
                        List<object?> xw = J.A(x["words"]);
                        List<object?> pal = J.A(x["palette"]);
                        dbk.U32((uint)(RefSize(x["indices"]) / 2), _big);
                        dbk.U32(0, _big);
                        U32(dbk, xw[0]);
                        dbk.U32((uint)(RefSize(x["vertices"]) / stride), _big);
                        dbk.U32(0, _big);
                        U32(dbk, xw[1]);
                        dbk.Add(J.U8((long)pal.Count));
                        foreach (object? p in pal)
                        {
                            dbk.Add(J.U8(p));
                        }
                        dbk.Add(Core.Bin.FromHex(J.S(x["palette_rest"])));
                        dbk.Add(J.U8(x["material"]));
                        dbk.Add(J.U8(x["created"]));
                        dbk.Add(J.U8(x["mask"]));
                        dbk.Add(J.U8(x["bone"]));
                        dbk.Add(Core.Bin.FromHex(J.S(x["tail"])));
                        Ptr($"{n}.draws", (0x50 * d) + 4, dn + ".indices");
                        Ptr($"{n}.draws", (0x50 * d) + 0x10, dn + ".vertices");
                    }
                    else
                    {
                        List<object?> xw = J.A(x["words"]);
                        List<object?> pal = J.A(x["palette"]);
                        dbk.U32((uint)(RefSize(x["indices"]) / 2), _big);
                        dbk.U32(0, _big);
                        U32(dbk, xw[0]);
                        dbk.U32((uint)(RefSize(x["skinned"]) / 32), _big);
                        dbk.U32(0, _big);
                        dbk.U32((uint)(RefSize(x["rigid"]) / 24), _big);
                        dbk.U32(0, _big);
                        U32(dbk, xw[1]);
                        dbk.Add(J.U8((long)pal.Count));
                        foreach (object? p in pal)
                        {
                            dbk.Add(J.U8(p));
                        }
                        dbk.Add(Core.Bin.FromHex(J.S(x["palette_rest"])));
                        foreach (string f in new[] { "material", "created", "b3b", "mask", "b3d", "bone", "b3f" })
                        {
                            dbk.Add(J.U8(x[f]));
                        }
                        Ptr($"{n}.draws", (0x40 * d) + 4, dn + ".indices");
                        Ptr($"{n}.draws", (0x40 * d) + 0x10, dn + ".skinned");
                        Ptr($"{n}.draws", (0x40 * d) + 0x18, dn + ".rigid");
                    }
                }
                if (_plat == Platform.PS3)
                {
                    List<object?> groups = J.A(m["groups"]);
                    for (int g = 0; g < groups.Count; g++)
                    {
                        JObj grp = J.O(groups[g]);
                        int gq = q + 20 + (24 * g);
                        List<object?> gw = J.A(grp["words"]);
                        hdr.PatchU32(gq, (uint)((grp["skinned"] is { } s ? RefSize(s) : 0) / 80), _big);
                        hdr.PatchU32(gq + 4, (uint)((grp["rigid"] is { } rg ? RefSize(rg) : 0) / 64), _big);
                        hdr.PatchU32(gq + 8, 0, _big);
                        hdr.PatchU32(gq + 12, 0, _big);
                        hdr.PatchU32(gq + 16, J.U(gw[0]), _big);
                        hdr.PatchU32(gq + 20, J.U(gw[1]), _big);
                    }
                    foreach ((string kindName, int off) in new[] { ("skinned", 8), ("rigid", 12) })
                    {
                        for (int g = 0; g < groups.Count; g++)
                        {
                            JObj grp = J.O(groups[g]);
                            if (grp[kindName] is not null)
                            {
                                Ptr("mesh", q + 20 + (24 * g) + off, $"{n}.g{g}.{kindName}");
                                Block($"{n}.g{g}.{kindName}", Raw(grp[kindName]));
                            }
                        }
                    }
                    foreach (int d in order)
                    {
                        Block($"{n}.d{d}.indices", Raw(J.O(draws[d])["indices"]));
                    }
                }
                else
                {
                    foreach (int d in order)
                    {
                        JObj x = J.O(draws[d]);
                        string dn = $"{n}.d{d}";
                        Block(dn + ".indices", Raw(x["indices"]));
                        foreach (string kindName in new[] { "vertices", "skinned", "rigid" })
                        {
                            if (x.Has(kindName))
                            {
                                Block($"{dn}.{kindName}", Raw(x[kindName]));
                            }
                        }
                    }
                }
            }
        }

        private void Skeletons(List<object?> sks)
        {
            for (int k = 0; k < sks.Count; k++)
            {
                if (sks[k] is not JObj sk)
                {
                    continue;
                }
                if (sk["link"] is string link)
                {
                    Block($"k{k}.link", Core.Bin.FromHex(link));
                }
                ByteWriter bones = new();
                List<object?> boneList = J.A(sk["bones"]);
                foreach (object? bo in boneList)
                {
                    JObj b = J.O(bo);
                    foreach (object? v in J.A(b["matrix"]))
                    {
                        F32(bones, v);
                    }
                    U32(bones, b["hash"]);
                    U32(bones, b["word"]);
                    bones.Add(J.U8(b["parent"]));
                    bones.Add(Core.Bin.FromHex(J.S(b["rest"])));
                }
                Block($"k{k}.bones", bones.Span);
                ByteWriter poses = new();
                foreach (object? p in J.A(sk["poses"]))
                {
                    foreach (object? v in J.A(p))
                    {
                        F32(poses, v);
                    }
                }
                Block($"k{k}.poses", poses.Span);
                List<object?> words = J.A(sk["words"]);
                ByteWriter head = new(32);
                head.Add("DDDD"u8);
                head.U32((uint)boneList.Count, _big);
                head.U32(0, _big);
                head.U32(0, _big);
                U32(head, words[0]);
                head.U32(0, _big);
                U32(head, words[1]);
                U32(head, words[2]);
                Block($"k{k}.header", head.Span);
                Ptr($"k{k}.header", 8, $"k{k}.bones");
                Ptr($"k{k}.header", 12, $"k{k}.poses");
                if (k != 0 && sk["link"] is not null)
                {
                    Ptr($"k{k}.header", 0x14, $"k{k}.link");
                }
                Ptr("header", 0x28 + (4 * k), $"k{k}.header");
            }
        }

        private void MeshGdt(JObj mesh)
        {
            List<object?> lods = J.A(mesh["lods"]);
            ByteWriter blk = Block("mesh", new byte[0x138]);
            blk.PatchU32(0, J.U(mesh["format"]), _big);
            blk.PatchU32(4, (uint)lods.Count, _big);
            List<object?> tail = J.A(mesh["tail"]);
            for (int t = 0; t < 4; t++)
            {
                blk.PatchU32(0x128 + (4 * t), J.U(tail[t]), _big);
            }
            int dsize = _plat == Platform.Wii ? 0x10 : 0x14;
            int stride = _plat is Platform.PS3 or Platform.Xbox360 ? 72 : 24;
            for (int l = 0; l < lods.Count; l++)
            {
                JObj lod = J.O(lods[l]);
                int q = 8 + (0x30 * l);
                List<object?> w = J.A(lod["words"]);
                uint vc = (uint)(RefSize(lod["vertices"]) / stride);
                Core.Bin.PutF32(blk.Span, q, J.D(lod["distance"]), _big);
                Core.Bin.PutF32(blk.Span, q + 4, J.D(lod["scale"]), _big);
                for (int z = 0; z < 5; z++)
                {
                    blk.PatchU32(q + 8 + (4 * z), 0, _big);
                }
                if (_plat is Platform.Wii or Platform.N3ds)
                {
                    uint[] tw = [J.U(w[0]), J.U(w[1]), vc, 0, J.U(w[2])];
                    for (int z = 0; z < 5; z++)
                    {
                        blk.PatchU32(q + 28 + (4 * z), tw[z], _big);
                    }
                    Ptr("mesh", q + 40, $"l{l}.vertices");
                }
                else
                {
                    uint[] tw = [vc, 0, J.U(w[0]), J.U(w[1]), J.U(w[2])];
                    for (int z = 0; z < 5; z++)
                    {
                        blk.PatchU32(q + 28 + (4 * z), tw[z], _big);
                    }
                    Ptr("mesh", q + 32, $"l{l}.vertices");
                }
                if (lod["draws"] is not List<object?> draws)
                {
                    Block($"l{l}.vertices", Raw(lod["vertices"]));
                    continue;
                }
                List<object?> lists = J.A(lod["lists"]);
                int n = 0;
                for (int k = 0; k < 5; k++)
                {
                    _fix.Add(("mesh", q + 8 + (4 * k), $"l{l}.draws", n * dsize));
                    n += k < 4 ? J.I(lists[k]) : 0;
                }
                ByteWriter db = Block($"l{l}.draws");
                for (int j = 0; j < draws.Count; j++)
                {
                    JObj d = J.O(draws[j]);
                    if (_plat == Platform.Wii)
                    {
                        U32(db, d["material"]);
                        db.U32(0, _big);
                        db.U32((uint)RefSize(d["display_list"]), _big);
                        U32(db, d["flags"]);
                        Ptr($"l{l}.draws", (j * dsize) + 4, $"l{l}.d{j}.dl");
                    }
                    else
                    {
                        U32(db, d["material"]);
                        db.U32((uint)(RefSize(d["indices"]) / 2), _big);
                        db.U32(0, _big);
                        U32(db, d["primitive"]);
                        U32(db, d["flags"]);
                        Ptr($"l{l}.draws", (j * dsize) + 8, $"l{l}.d{j}.indices");
                    }
                }
                List<int> order = lod.Get("data_order") is List<object?> { Count: > 0 } dataOrder
                    ? [.. dataOrder.Select(J.I)]
                    : StableSort(Enumerable.Range(0, draws.Count), j => GdtMaterial(_plat, J.O(draws[j])));
                foreach (int j in order)
                {
                    JObj d = J.O(draws[j]);
                    if (_plat == Platform.Wii)
                    {
                        Block($"l{l}.d{j}.dl", Raw(d["display_list"]));
                    }
                    else
                    {
                        Block($"l{l}.d{j}.indices", Raw(d["indices"]));
                    }
                }
                Block($"l{l}.vertices", Raw(lod["vertices"]));
            }
        }

        private (Dictionary<string, int> Off, int End) Offsets(JObj? padded)
        {
            Dictionary<string, int> off = new(StringComparer.Ordinal);
            int o = 0;
            foreach ((string name, ByteWriter data, int align) in Blocks)
            {
                o = Core.Bin.Align(o, align) + (padded?.Get(name) is { } p ? J.I(p) : 0);
                off[name] = o;
                o += data.Length;
            }
            return (off, o);
        }

        public byte[] Build()
        {
            Layout();
            (Dictionary<string, int> off, int end) = Offsets(_doc.Get("padded") as JObj);
            byte[] output = new byte[end + (int)J.L(_doc.Get("end_padding", 0L))];
            foreach ((string name, ByteWriter data, _) in Blocks)
            {
                data.Span.CopyTo(output.AsSpan(off[name]));
            }
            foreach ((string block, int pos, string target, int add) in _fix)
            {
                Core.Bin.PutU32(output, off[block] + pos, (uint)(off[target] + add), _big);
            }
            foreach ((string block, int pos, string target) in _endFix)
            {
                ByteWriter data = Blocks.First(b => b.Name == target).Data;
                Core.Bin.PutU32(output, off[block] + pos, (uint)(off[target] + data.Length), _big);
            }
            return output;
        }
    }
}
