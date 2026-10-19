using DWSSK.Formats.Core;
namespace DWSSK.Formats.Data;
internal static class P2b
{
    private static byte[] AsciiUpper(byte[] s)
    {
        byte[] o = (byte[])s.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            if (o[i] is >= (byte)'a' and <= (byte)'z')
            {
                o[i] -= 32;
            }
        }
        return o;
    }
    private static byte[] AsciiLower(byte[] s)
    {
        byte[] o = (byte[])s.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            if (o[i] is >= (byte)'A' and <= (byte)'Z')
            {
                o[i] += 32;
            }
        }
        return o;
    }
    public static uint Scheme(string scheme, byte[] s)
    {
        return scheme switch
        {
            "crc" => Hashes.Crc32(s),
            "upper" => Hashes.Crc32(AsciiUpper(s)),
            "path" => Hashes.Crc32([.. AsciiUpper(s).Select(c => c == (byte)'/' ? (byte)'\\' : c)]),
            "wwise" => Hashes.Fnv1(AsciiLower(s)),
            _ => throw new KeyNotFoundException($"hash scheme '{scheme}'"),
        };
    }
    private static readonly string[] s_schemes = ["crc", "upper", "path", "wwise"];
    public static string SchemeOf(string name, uint h)
    {
        byte[] e = Bin.Latin1(name);
        foreach (string k in s_schemes)
        {
            if (Scheme(k, e) == h)
            {
                return k;
            }
        }
        throw new InvalidDataException($"no hash scheme gives 0x{h:x} for '{name}'");
    }
    public static int EnumWidth(bool big, IReadOnlyList<long> values)
    {
        if (big || values.Count == 0)
        {
            return 4;
        }
        long lo = values.Min();
        long hi = values.Max();
        if (lo >= -128 && hi <= 255)
        {
            return 1;
        }
        return lo >= -32768 && hi <= 65535 ? 2 : 4;
    }
    public static bool IsP2b(ReadOnlySpan<byte> b)
    {
        return b.Length >= 0x24 && ((b[0] == 0 && b[1] == 0x0A) || (b[0] == 0x0A && b[1] == 0));
    }
    private sealed record Rec(int Kind, byte[] Junk, uint Type, uint Name, uint Offset);
    private sealed record Desc(Rec Rec, Desc? Sub);
    public static JObj Read(byte[] b)
    {
        return new Reader(b).ToJson();
    }
    private sealed class Reader
    {
        private readonly byte[] _b;
        private readonly bool _big;
        private readonly Dictionary<uint, string> _names = [];
        private readonly List<(uint H, int O, string S)> _strings = [];
        private readonly Dictionary<uint, (int Fo, List<Rec> Recs)> _types = [];
        private readonly List<uint> _typeOrder = [];
        private readonly Dictionary<uint, List<Desc>> _fields = [];
        private readonly Dictionary<uint, (int Vo, List<(uint H, int V)> Values)> _enums = [];
        private readonly List<uint> _enumOrder = [];
        private readonly Dictionary<uint, (int Size, int Align)> _sizes = [];
        private Dictionary<uint, string?> _roots = [];
        private readonly int _version;
        private readonly int _no;
        private readonly int _oo;
        public Reader(byte[] b)
        {
            _b = b;
            _big = b.Length >= 2 && b[0] == 0 && b[1] == 0x0A;
            _version = Bin.U16(b, 0, _big);
            int nt = (int)U(4);
            int ot = (int)U(8);
            _no = (int)U(12);
            _oo = (int)U(16);
            int ne = (int)U(20);
            int oe = (int)U(24);
            int ns = (int)U(28);
            int os = (int)U(32);
            for (int i = 0; i < ns; i++)
            {
                uint h = U(os + (i * 8));
                int o = (int)U(os + (i * 8) + 4);
                string s = Bin.CString(b, o);
                _strings.Add((h, o, s));
                _names.TryAdd(h, s);
            }
            for (int i = 0; i < nt; i++)
            {
                uint th = U(ot + (i * 12));
                int nf = (int)U(ot + (i * 12) + 4);
                int fo = (int)U(ot + (i * 12) + 8);
                List<Rec> recs = [];
                for (int j = 0; j < nf; j++)
                {
                    recs.Add(Record(fo + (j * 16)));
                }
                _types[th] = (fo, recs);
                _typeOrder.Add(th);
            }
            foreach ((uint th, (int _, List<Rec> recs)) in _types)
            {
                _fields[th] = Descs(recs);
            }
            for (int i = 0; i < ne; i++)
            {
                uint eh = U(oe + (i * 12));
                int nv = (int)U(oe + (i * 12) + 4);
                int vo = (int)U(oe + (i * 12) + 8);
                List<(uint, int)> values = [];
                for (int j = 0; j < nv; j++)
                {
                    values.Add((U(vo + (j * 8)), Bin.I32(b, vo + (j * 8) + 4, _big)));
                }
                _enums[eh] = (vo, values);
                _enumOrder.Add(eh);
            }
        }
        private uint U(int o)
        {
            return Bin.U32(_b, o, _big);
        }
        private Rec Record(int o)
        {
            Bin.Need(_b, o, 16);
            return new Rec(_b[o], _b[(o + 1)..(o + 4)], U(o + 4), U(o + 8), U(o + 12));
        }
        private static List<Desc> Descs(List<Rec> recs)
        {
            List<Desc> output = [];
            int j = 0;
            while (j < recs.Count)
            {
                (Desc d, j) = DescAt(recs, j);
                output.Add(d);
            }
            return output;
        }
        private static (Desc, int) DescAt(List<Rec> recs, int j)
        {
            Rec r = recs[j];
            if (r.Kind is 10 or 12)
            {
                if (j + 1 >= recs.Count)
                {
                    throw new PyIndexException("list index out of range");
                }
                (Desc sub, int nj) = DescAt(recs, j + 1);
                return (new Desc(r, sub), nj);
            }
            return (new Desc(r, null), j + 1);
        }
        private Desc DescAtOffset(int o)
        {
            List<Rec> recs = [];
            int j = o;
            while (true)
            {
                recs.Add(Record(j));
                if (recs[^1].Kind is not (10 or 12))
                {
                    break;
                }
                j += 16;
            }
            return DescAt(recs, 0).Item1;
        }
        private string? Name(uint h)
        {
            return h == 0 ? null : _names.TryGetValue(h, out string? s) ? s : $"#{Py.Hex8(h)}";
        }
        private List<Desc> Fields(uint th)
        {
            return _fields.TryGetValue(th, out List<Desc>? f) ? f : throw new KeyNotFoundException($"type {Py.Hex8(th)} is not defined");
        }
        private int TypeSize(uint th)
        {
            if (!_sizes.TryGetValue(th, out (int Size, int Align) s))
            {
                int end = 0;
                foreach (Desc d in Fields(th))
                {
                    end = Math.Max(end, (int)d.Rec.Offset + DescSize(d).Size);
                }
                int al = TypeAlign(th);
                s = (Bin.CeilDiv(end, al) * al, al);
                _sizes[th] = s;
            }
            return s.Size;
        }
        private int TypeAlign(uint th)
        {
            int al = 1;
            bool any = false;
            foreach (Desc d in Fields(th))
            {
                int a = DescSize(d).Align;
                al = any ? Math.Max(al, a) : a;
                any = true;
            }
            return al;
        }
        private int EnumWidthOf(uint t)
        {
            return EnumWidth(_big, _enums.TryGetValue(t, out (int, List<(uint, int)> Values) e) ? [.. e.Values.Select(x => (long)x.Item2)] : []);
        }
        private (int Size, int Align) DescSize(Desc d)
        {
            int k = d.Rec.Kind;
            uint t = d.Rec.Type;
            if (k == 1)
            {
                return (TypeSize(t), TypeAlign(t));
            }
            if (k == 14)
            {
                int w = EnumWidthOf(t);
                return (w, w);
            }
            if (k == 12)
            {
                return (8, 4);
            }
            return k is 8 or 9 ? (1, 1) : (4, 4);
        }
        private object? Value(Desc d, int addr)
        {
            Rec r = d.Rec;
            int k = r.Kind;
            uint t = r.Type;
            if (k == 1)
            {
                JObj o = [];
                foreach (Desc fd in Fields(t))
                {
                    string? nm = Name(fd.Rec.Name);
                    o[string.IsNullOrEmpty(nm) ? $"@{fd.Rec.Offset:x}" : nm] = Value(fd, addr + (int)fd.Rec.Offset);
                }
                return o;
            }
            if (k == 12)
            {
                uint cnt = U(addr);
                uint ptr = U(addr + 4);
                if (cnt == 0)
                {
                    return ptr != 0 ? new JObj { ["@empty"] = (long)ptr } : new List<object?>();
                }
                int stride = DescSize(d.Sub!).Size;
                List<object?> list = new((int)Math.Min(cnt, 1 << 16));
                for (long i = 0; i < cnt; i++)
                {
                    list.Add(Value(d.Sub!, (int)(ptr + (i * stride))));
                }
                return list;
            }
            long v;
            if (k == 14)
            {
                int w = EnumWidthOf(t);
                v = w switch
                {
                    1 => Bin.U8(_b, addr),
                    2 => Bin.U16(_b, addr, _big),
                    _ => Bin.I32(_b, addr, _big),
                };
            }
            else
            {
                v = k switch
                {
                    2 or 3 or 10 or 13 or 17 or 18 or 19 => U(addr),
                    4 => Bin.I32(_b, addr, _big),
                    8 or 9 => Bin.U8(_b, addr),
                    _ => throw new KeyNotFoundException($"{k}"),
                };
            }
            switch (k)
            {
                case 10:
                    if (v == 0)
                    {
                        return null;
                    }
                    if (_roots.TryGetValue((uint)v, out string? root))
                    {
                        return new JObj { ["@ref"] = root };
                    }
                    return new JObj { ["@ptr"] = Value(d.Sub!, (int)v) };
                case 2:
                {
                    float f = BitConverter.UInt32BitsToSingle((uint)v);
                    return float.IsFinite(f) ? (double)f : new JObj { ["@f32"] = Bin.Hex(_b.AsSpan(addr, 4)) };
                }
                case 9:
                    return v is 0 or 1 ? v == 1 : new JObj { ["@bool"] = v };
                case 13:
                    return v == 0 ? null : Bin.CString(_b, (int)v);
                case 14:
                    if (_enums.TryGetValue(t, out (int, List<(uint H, int V)> Values) e))
                    {
                        foreach ((uint h, int x) in e.Values)
                        {
                            if (x == v)
                            {
                                return Name(h);
                            }
                        }
                    }
                    return v;
                case 17 or 18 or 19:
                    return v != 0 ? Name((uint)v) : 0L;
                default:
                    return v;
            }
        }
        private JObj RecJson(Rec r)
        {
            return new JObj
            {
                ["kind"] = (long)r.Kind,
                ["junk"] = Bin.Hex(r.Junk),
                ["type"] = Name(r.Type),
                ["name"] = Name(r.Name),
                ["offset"] = (long)r.Offset,
            };
        }
        private JObj DescJson(Desc d)
        {
            JObj o = RecJson(d.Rec);
            if (d.Sub is not null)
            {
                o["target"] = DescJson(d.Sub);
            }
            return o;
        }
        public JObj ToJson()
        {
            List<object?> types = [];
            foreach (uint th in _typeOrder)
            {
                (int fo, List<Rec> recs) = _types[th];
                int at = fo + (16 * recs.Count);
                types.Add(new JObj
                {
                    ["name"] = Name(th),
                    ["records"] = J.List(recs.Select(r => (object?)RecJson(r))),
                    ["end_junk"] = Bin.Hex(Bin.Slice(_b, at + 1, at + 4)),
                });
            }
            List<object?> enums = [];
            foreach (uint eh in _enumOrder)
            {
                enums.Add(new JObj
                {
                    ["name"] = Name(eh),
                    ["values"] = J.List(_enums[eh].Values.Select(x => (object?)new List<object?> { Name(x.H), (long)x.V })),
                });
            }
            List<object?> objects = [];
            List<object?> strings = [];
            foreach ((uint h, _, string s) in _strings)
            {
                string sch = SchemeOf(s, h);
                strings.Add(sch == "crc" ? s : new List<object?> { s, sch });
            }
            JObj doc = new()
            {
                ["endian"] = _big ? "big" : "little",
                ["version"] = (long)_version,
                ["types"] = types,
                ["enums"] = enums,
                ["objects"] = objects,
                ["strings"] = strings,
            };
            List<(int Ro, int Do)> objs = [];
            for (int i = 0; i < _no; i++)
            {
                objs.Add(((int)U(_oo + (i * 8)), (int)U(_oo + (i * 8) + 4)));
            }
            _roots = [];
            foreach ((int ro, int dataOff) in objs)
            {
                _roots[(uint)dataOff] = Name(Record(ro).Name);
            }
            foreach ((int ro, int dataOff) in objs)
            {
                Desc d = DescAtOffset(ro);
                objects.Add(new JObj { ["descriptor"] = DescJson(d), ["value"] = Value(d, dataOff) });
            }
            return doc;
        }
    }
    public static byte[] Write(JObj doc)
    {
        return new Writer(doc).Build();
    }
    private sealed class Buf(int size)
    {
        public byte[] Data { get; } = new byte[size];
    }
    private sealed class Writer
    {
        private readonly JObj _doc;
        private readonly bool _big;
        private readonly ByteWriter _out = new(1 << 14);
        private readonly Dictionary<string, JObj> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JObj> _enums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _known = new(StringComparer.Ordinal);
        private List<(Buf? B, int O, Func<int> Fn)> _queue = [];
        private List<(Buf? B, int O, string N)> _refFixups = [];
        public Writer(JObj doc)
        {
            _doc = doc;
            _big = J.S(doc["endian"]) == "big";
            foreach (object? t in J.A(doc["types"]))
            {
                JObj o = J.O(t);
                _types[Key(o["name"])] = o;
            }
            foreach (object? e in J.A(doc["enums"]))
            {
                JObj o = J.O(e);
                _enums[Key(o["name"])] = o;
            }
            foreach (object? s in J.A(doc["strings"]))
            {
                (string name, string sch) = s is string str ? (str, "crc") : (J.S(J.A(s)[0]), J.S(J.A(s)[1]));
                _known.TryAdd(name, Scheme(sch, Bin.Latin1(name)));
            }
        }
        private static string Key(object? name)
        {
            return name as string ?? "\u0000null";
        }
        private uint H(object? name, int kind = 0)
        {
            if (name is null || (name is long l && l == 0) || (name is bool b && !b))
            {
                return 0;
            }
            string s = name as string ?? throw new InvalidDataException($"a name is a string, found {J.Describe(name)}");
            if (s.StartsWith('#'))
            {
                return Py.ParseHex(s[1..]);
            }
            if (!_known.TryGetValue(s, out uint h))
            {
                string sch = kind switch
                {
                    18 => "wwise",
                    19 => "path",
                    _ => "crc",
                };
                h = Scheme(sch, Bin.Latin1(s));
                _known[s] = h;
            }
            return h;
        }
        private void Pad(int a = 0x20)
        {
            _out.Pad(a);
        }
        private void Put32(uint v)
        {
            _out.U32(v, _big);
        }
        private void Patch32(int at, uint v)
        {
            _out.PatchU32(at, v, _big);
        }
        private void RecOut(JObj r)
        {
            _out.Add(J.U8(r["kind"]));
            _out.Add(Bin.FromHex(J.S(r["junk"])));
            Put32(H(r["type"]));
            Put32(H(r["name"]));
            Put32(J.U(r["offset"]));
        }
        private sealed record WDesc(JObj Rec, WDesc? Sub);
        private static List<WDesc> Descs(List<object?> recs)
        {
            List<WDesc> output = [];
            int j = 0;
            while (j < recs.Count)
            {
                (WDesc d, j) = DescAt(recs, j);
                output.Add(d);
            }
            return output;
        }
        private static (WDesc, int) DescAt(List<object?> recs, int j)
        {
            JObj r = J.O(recs[j]);
            if (J.L(r["kind"]) is 10 or 12)
            {
                if (j + 1 >= recs.Count)
                {
                    throw new PyIndexException("list index out of range");
                }
                (WDesc sub, int nj) = DescAt(recs, j + 1);
                return (new WDesc(r, sub), nj);
            }
            return (new WDesc(r, null), j + 1);
        }
        private static WDesc FromJson(JObj d)
        {
            return new WDesc(d, d.Has("target") ? FromJson(J.O(d["target"])) : null);
        }
        private List<WDesc> TypeFields(object? name)
        {
            return _types.TryGetValue(Key(name), out JObj? t)
                ? Descs(J.A(t["records"]))
                : throw new KeyNotFoundException($"type '{name}' is not defined");
        }
        private int EnumWidthOf(object? name)
        {
            return _enums.TryGetValue(Key(name), out JObj? e)
                ? EnumWidth(_big, [.. J.A(e["values"]).Select(v => J.L(J.A(v)[1]))])
                : EnumWidth(_big, []);
        }
        private int Size(WDesc d)
        {
            long k = J.L(d.Rec["kind"]);
            return k switch
            {
                1 => TypeSize(d.Rec["type"]),
                14 => EnumWidthOf(d.Rec["type"]),
                12 => 8,
                8 or 9 => 1,
                _ => 4,
            };
        }
        private int Align(WDesc d)
        {
            long k = J.L(d.Rec["kind"]);
            if (k == 1)
            {
                List<WDesc> fs = TypeFields(d.Rec["type"]);
                return fs.Count == 0 ? 1 : fs.Max(Align);
            }
            return k == 14 ? EnumWidthOf(d.Rec["type"]) : k is 8 or 9 ? 1 : 4;
        }
        private int TypeSize(object? name)
        {
            List<WDesc> fs = TypeFields(name);
            int end = fs.Count == 0 ? 0 : fs.Max(f => (int)J.L(f.Rec["offset"]) + Size(f));
            int al = fs.Count == 0 ? 1 : fs.Max(Align);
            return Bin.CeilDiv(end, al) * al;
        }
        private void Encode(WDesc d, object? v, Buf buf, int off)
        {
            long k = J.L(d.Rec["kind"]);
            switch (k)
            {
                case 1:
                {
                    JObj obj = J.O(v);
                    foreach (WDesc f in TypeFields(d.Rec["type"]))
                    {
                        string nm = f.Rec["name"] is string { Length: > 0 } s ? s : $"@{J.L(f.Rec["offset"]):x}";
                        Encode(f, obj[nm], buf, off + (int)J.L(f.Rec["offset"]));
                    }
                    return;
                }
                case 12:
                {
                    if (v is JObj empty)
                    {
                        Bin.PutU32(buf.Data, off, 0, _big);
                        Bin.PutU32(buf.Data, off + 4, J.U(empty["@empty"]), _big);
                        return;
                    }
                    List<object?> items = J.A(v);
                    Bin.PutU32(buf.Data, off, (uint)items.Count, _big);
                    if (items.Count > 0)
                    {
                        WDesc sub = d.Sub!;
                        _queue.Add((buf, off + 4, () => WriteArray(sub, items)));
                    }
                    return;
                }
                case 10:
                {
                    if (v is null)
                    {
                        Bin.PutU32(buf.Data, off, 0, _big);
                    }
                    else if (J.O(v).Has("@ref"))
                    {
                        _refFixups.Add((buf, off, J.O(v)["@ref"] as string ?? "\u0000null"));
                    }
                    else
                    {
                        WDesc sub = d.Sub!;
                        object? target = J.O(v)["@ptr"];
                        _queue.Add((buf, off, () => WriteBlock(sub, target)));
                    }
                    return;
                }
                case 13:
                {
                    if (v is null)
                    {
                        Bin.PutU32(buf.Data, off, 0, _big);
                    }
                    else
                    {
                        string s = J.S(v);
                        _queue.Add((buf, off, () => WriteChars(s)));
                    }
                    return;
                }
                case 2:
                    if (v is JObj bits)
                    {
                        byte[] raw = Bin.FromHex(J.S(bits["@f32"]));
                        raw.CopyTo(buf.Data, off);
                    }
                    else
                    {
                        Bin.PutF32(buf.Data, off, J.D(v), _big);
                    }
                    return;
                case 9:
                    buf.Data[off] = J.U8(v is JObj bo ? bo["@bool"] : J.L(v));
                    return;
                case 14:
                {
                    long value;
                    if (v is string named)
                    {
                        JObj e = _enums.TryGetValue(Key(d.Rec["type"]), out JObj? en) ? en : throw new KeyNotFoundException($"enum '{d.Rec["type"]}'");
                        value = J.A(e["values"]).Select(J.A).Where(x => x[0] as string == named).Select(x => J.L(x[1])).DefaultIfEmpty(long.MinValue).First();
                        if (value == long.MinValue)
                        {
                            throw new InvalidOperationException($"'{named}' is not a value of enum '{d.Rec["type"]}'");
                        }
                    }
                    else
                    {
                        value = J.L(v);
                    }
                    int w = EnumWidthOf(d.Rec["type"]);
                    if (w == 1)
                    {
                        buf.Data[off] = (byte)(value & 0xFF);
                    }
                    else if (w == 2)
                    {
                        Bin.PutU16(buf.Data, off, (uint)(value & 0xFFFF), _big);
                    }
                    else
                    {
                        Bin.PutU32(buf.Data, off, (uint)J.I32(value), _big);
                    }
                    return;
                }
                case 17 or 18 or 19:
                    Bin.PutU32(buf.Data, off, H(v, (int)k), _big);
                    return;
                case 3:
                    Bin.PutU32(buf.Data, off, J.U(v), _big);
                    return;
                case 4:
                    Bin.PutU32(buf.Data, off, (uint)J.I32(v), _big);
                    return;
                case 8:
                    buf.Data[off] = J.U8(v);
                    return;
                default:
                    throw new KeyNotFoundException($"{k}");
            }
        }
        private int WriteArray(WDesc sub, List<object?> items)
        {
            int stride = Size(sub);
            Pad(Math.Max(4, Align(sub)));
            int start = _out.Length;
            Buf buf = new(stride * items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Encode(sub, items[i], buf, i * stride);
            }
            _out.Add(buf.Data);
            Attach(buf, start);
            return start;
        }
        private int WriteBlock(WDesc d, object? v)
        {
            Pad(Math.Max(4, Align(d)));
            int start = _out.Length;
            Buf buf = new(Size(d));
            Encode(d, v, buf, 0);
            _out.Add(buf.Data);
            Attach(buf, start);
            return start;
        }
        private int WriteChars(string s)
        {
            Pad(4);
            int start = _out.Length;
            _out.Add(Bin.Latin1(s));
            _out.Add(0);
            return start;
        }
        private void Attach(Buf buf, int start)
        {
            _queue = [.. _queue.Select(q => ReferenceEquals(q.B, buf) ? (null, q.O + start, q.Fn) : q)];
            _refFixups = [.. _refFixups.Select(q => ReferenceEquals(q.B, buf) ? (null, q.O + start, q.N) : q)];
        }
        private void Drain()
        {
            while (_queue.Count > 0)
            {
                (Buf? b, int o, Func<int> fn) = _queue[0];
                _queue.RemoveAt(0);
                if (b is not null)
                {
                    throw new InvalidOperationException("a pointer was queued against a block that was never placed");
                }
                Patch32(o, (uint)fn());
            }
        }
        public byte[] Build()
        {
            List<object?> types = J.A(_doc["types"]);
            List<object?> objects = J.A(_doc["objects"]);
            List<object?> enums = J.A(_doc["enums"]);
            List<object?> strings = J.A(_doc["strings"]);
            _refFixups = [];
            _out.U16(J.U16(_doc["version"]), _big);
            _out.U16(0, _big);
            _out.Zeros(32);
            int typesAt = _out.Length;
            _out.Zeros(12 * types.Count);
            for (int i = 0; i < types.Count; i++)
            {
                JObj t = J.O(types[i]);
                List<object?> recs = J.A(t["records"]);
                int fo = _out.Length;
                Patch32(typesAt + (12 * i), H(t["name"]));
                Patch32(typesAt + (12 * i) + 4, (uint)recs.Count);
                Patch32(typesAt + (12 * i) + 8, (uint)fo);
                foreach (object? r in recs)
                {
                    RecOut(J.O(r));
                }
                _out.Add(0);
                _out.Add(Bin.FromHex(J.S(t["end_junk"])));
                _out.Zeros(12);
                Pad();
            }
            Pad();
            int objsAt = _out.Length;
            _out.Zeros(8 * objects.Count);
            Dictionary<string, int> roots = new(StringComparer.Ordinal);
            List<int> datas = [];
            foreach (object? oo in objects)
            {
                JObj o = J.O(oo);
                WDesc d = FromJson(J.O(o["descriptor"]));
                int start = _out.Length;
                Buf buf = new(Size(d));
                Encode(d, o["value"], buf, 0);
                _out.Add(buf.Data);
                Attach(buf, start);
                Pad();
                roots[Key(J.O(o["descriptor"])["name"])] = start;
                datas.Add(start);
            }
            for (int i = 0; i < objects.Count; i++)
            {
                int ro = _out.Length;
                JObj? x = J.O(J.O(objects[i])["descriptor"]);
                while (x is not null)
                {
                    RecOut(x);
                    x = x.Get("target") as JObj;
                }
                Patch32(objsAt + (8 * i), (uint)ro);
                Patch32(objsAt + (8 * i) + 4, (uint)datas[i]);
            }
            Drain();
            foreach ((Buf? b, int o, string n) in _refFixups)
            {
                if (b is not null)
                {
                    throw new InvalidOperationException("a reference was queued against a block that was never placed");
                }
                Patch32(o, (uint)(roots.TryGetValue(n, out int at) ? at : throw new KeyNotFoundException($"no root object named '{n}'")));
            }
            Pad();
            int enumsAt = _out.Length;
            _out.Zeros(12 * enums.Count);
            for (int i = 0; i < enums.Count; i++)
            {
                JObj e = J.O(enums[i]);
                List<object?> values = J.A(e["values"]);
                int vo = _out.Length;
                Patch32(enumsAt + (12 * i), H(e["name"]));
                Patch32(enumsAt + (12 * i) + 4, (uint)values.Count);
                Patch32(enumsAt + (12 * i) + 8, (uint)vo);
                foreach (object? v in values)
                {
                    List<object?> pair = J.A(v);
                    Put32(H(pair[0]));
                    _out.I32(J.I32(pair[1]), _big);
                }
                Pad();
            }
            int stringsAt = _out.Length;
            _out.Zeros(8 * strings.Count);
            for (int i = 0; i < strings.Count; i++)
            {
                string name = strings[i] is string s ? s : J.S(J.A(strings[i])[0]);
                int so = _out.Length;
                Patch32(stringsAt + (8 * i), _known[name]);
                Patch32(stringsAt + (8 * i) + 4, (uint)so);
                _out.Add(Bin.Latin1(name));
                _out.Add(0);
                Pad(4);
            }
            Pad();
            uint[] head = [(uint)types.Count, (uint)typesAt, (uint)objects.Count, (uint)objsAt, (uint)enums.Count, (uint)enumsAt, (uint)strings.Count, (uint)stringsAt];
            for (int i = 0; i < 8; i++)
            {
                Patch32(4 + (4 * i), head[i]);
            }
            return _out.ToArray();
        }
    }
}
