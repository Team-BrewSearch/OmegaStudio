using System.Globalization;
using System.IO.Compression;
using System.Text;
using DWSSK.Formats.Core;
namespace DWSSK.Formats.Data;
internal static class Lcl
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    public static bool Big(ReadOnlySpan<byte> b)
    {
        return Bin.U8(b, 0) == 0;
    }
    public static JObj Read(byte[] b)
    {
        bool big = Big(b);
        int n = (int)Bin.U32(b, 0, big);
        List<object?> strings = [];
        for (int i = 0; i < n; i++)
        {
            uint h = Bin.U32(b, 4 + (i * 16), big);
            int off = (int)Bin.U32(b, 8 + (i * 16), big);
            uint tag = Bin.U32(b, 16 + (i * 16), big);
            int end = off < b.Length ? b.AsSpan(off).IndexOf((byte)0) : -1;
            if (end < 0)
            {
                throw new InvalidDataException($"string {i} at 0x{off:x} has no terminating NUL");
            }
            strings.Add(new JObj
            {
                ["key"] = $"#{Py.Hex8(h)}",
                ["hash"] = Py.Hex8(h),
                ["tag"] = (long)tag,
                ["text"] = s_utf8.GetString(b, off, end),
            });
        }
        return new JObj { ["endian"] = big ? "big" : "little", ["strings"] = strings };
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) != "little";
        List<object?> recs = J.A(doc["strings"]);
        ByteWriter head = new();
        head.U32((uint)recs.Count, big);
        ByteWriter data = new();
        int baseOff = 4 + (16 * recs.Count);
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        foreach (object? ro in recs.OrderByDescending(r => Py.ParseHex(J.S(J.O(r)["hash"]))))
        {
            JObj r = J.O(ro);
            string t = J.S(r["text"]);
            if (!seen.TryGetValue(t, out int at))
            {
                at = baseOff + data.Length;
                seen[t] = at;
                data.Add(s_utf8.GetBytes(t));
                data.Add(0);
            }
            head.U32(Py.ParseHex(J.S(r["hash"])), big);
            head.U32((uint)at, big);
            head.U32(0, big);
            head.U32(J.U(r["tag"]), big);
        }
        head.Add(data.Span);
        return head.ToArray();
    }
}
internal static class Fkd
{
    public static bool IsFkd(ReadOnlySpan<byte> b)
    {
        return b.Length >= 0x38 && b[..4].SequenceEqual("FKD."u8);
    }
    public static JObj Read(byte[] b)
    {
        float v = Bin.F32(b, 0x10, true);
        bool big = v > 0.5 && v < 4096;
        List<object?> metrics = J.Doubles(Bin.F32s(b, 0x10, 3, big));
        List<object?> ranges = [];
        List<int> codes = [];
        for (int i = 0; i < 7; i++)
        {
            int lo = Bin.U16(b, 0x1C + (i * 4), big);
            int hi = Bin.U16(b, 0x1E + (i * 4), big);
            ranges.Add(new List<object?> { (long)lo, (long)hi });
            if (lo != 0 || hi != 0)
            {
                for (int cp = lo; cp <= hi; cp++)
                {
                    codes.Add(cp);
                }
            }
        }
        int n = (b.Length - 0x38) / 8;
        if ((b.Length - 0x38) % 8 != 0 || n > codes.Count)
        {
            throw new InvalidDataException("glyph table does not match the ranges");
        }
        List<object?> glyphs = [];
        for (int i = 0; i < n; i++)
        {
            int o = 0x38 + (i * 8);
            glyphs.Add(new JObj
            {
                ["char"] = ((char)codes[i]).ToString(),
                ["code"] = (long)codes[i],
                ["x"] = (long)Bin.U16(b, o, big),
                ["y"] = (long)Bin.U16(b, o + 2, big),
                ["advance"] = (long)Bin.U16(b, o + 4, big),
                ["height"] = (long)Bin.U16(b, o + 6, big),
            });
        }
        return new JObj
        {
            ["endian"] = big ? "big" : "little",
            ["header"] = Bin.Hex(b.AsSpan(4, 12)),
            ["metrics"] = metrics,
            ["ranges"] = ranges,
            ["glyphs"] = glyphs,
        };
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        ByteWriter o = new();
        o.Add("FKD."u8);
        o.Add(Bin.FromHex(J.S(doc["header"])));
        List<object?> metrics = J.A(doc["metrics"]);
        if (metrics.Count != 3)
        {
            throw new StructException("pack expected 3 items for packing");
        }
        foreach (object? m in metrics)
        {
            o.F32(J.D(m), big);
        }
        foreach (object? r in J.A(doc["ranges"]))
        {
            List<object?> pair = J.A(r);
            o.U16(J.U16(pair[0]), big);
            o.U16(J.U16(pair[1]), big);
        }
        foreach (object? g in J.A(doc["glyphs"]))
        {
            JObj x = J.O(g);
            o.U16(J.U16(x["x"]), big);
            o.U16(J.U16(x["y"]), big);
            o.U16(J.U16(x["advance"]), big);
            o.U16(J.U16(x["height"]), big);
        }
        return o.ToArray();
    }
}
internal static class Cam
{
    private static readonly (string Name, int N)[] s_tracks = [("position", 4), ("direction", 4), ("fov", 2)];
    public static JObj Read(byte[] b)
    {
        bool big = b.Length >= 8 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0x1C;
        uint[] head = Bin.U32s(b, 0, 7, big);
        JObj o = new() { ["endian"] = big ? "big" : "little", ["version"] = (long)head[0] };
        for (int i = 0; i < 3; i++)
        {
            (string name, int n) = s_tracks[i];
            int off = (int)head[1 + (2 * i)];
            int cnt = (int)head[2 + (2 * i)];
            List<object?> keys = [];
            for (int k = 0; k < cnt; k++)
            {
                keys.Add(J.Doubles(Bin.F32s(b, off + (k * 4 * n), n, big)));
            }
            o[name] = keys;
        }
        return o;
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        ByteWriter head = new();
        head.U32(J.U(doc["version"]), big);
        ByteWriter data = new();
        foreach ((string name, int n) in s_tracks)
        {
            List<object?> keys = J.A(doc[name]);
            head.U32((uint)(0x1C + data.Length), big);
            head.U32((uint)keys.Count, big);
            foreach (object? key in keys)
            {
                List<object?> v = J.A(key);
                if (v.Count != n)
                {
                    throw new StructException($"pack expected {n} items for packing (got {v.Count})");
                }
                foreach (object? x in v)
                {
                    data.F32(J.D(x), big);
                }
            }
        }
        head.Add(data.Span);
        return head.ToArray();
    }
}
internal static class Csd
{
    public static bool IsCsd(ReadOnlySpan<byte> b)
    {
        return b.Length >= 12 && b[..4].SequenceEqual("STD "u8);
    }
    public static JObj Read(byte[] b)
    {
        bool big = b.Length >= 6 && b[4] == 0 && b[5] == 0;
        uint ver = Bin.U32(b, 4, big);
        int n = (int)Bin.U32(b, 8, big);
        List<object?> surfaces = [];
        for (int k = 0; k < n; k++)
        {
            uint[] v = Bin.U32s(b, 12 + (k * 16), 4, big);
            surfaces.Add(new JObj
            {
                ["name"] = Py.Hex8(v[0]),
                ["flags"] = (long)v[1],
                ["a"] = Py.Hex8(v[2]),
                ["b"] = Py.Hex8(v[3]),
            });
        }
        return new JObj { ["endian"] = big ? "big" : "little", ["version"] = (long)ver, ["surfaces"] = surfaces };
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        List<object?> surfaces = J.A(doc["surfaces"]);
        ByteWriter o = new();
        o.Add("STD "u8);
        o.U32(J.U(doc["version"]), big);
        o.U32((uint)surfaces.Count, big);
        foreach (object? s in surfaces.OrderBy(s => Py.ParseHex(J.S(J.O(s)["name"]))))
        {
            JObj x = J.O(s);
            o.U32(Py.ParseHex(J.S(x["name"])), big);
            o.U32(J.U(x["flags"]), big);
            o.U32(Py.ParseHex(J.S(x["a"])), big);
            o.U32(Py.ParseHex(J.S(x["b"])), big);
        }
        return o.ToArray();
    }
}
internal static class Nav
{
    public static bool IsNav(ReadOnlySpan<byte> b)
    {
        return b.Length >= 12 && b[..4].SequenceEqual("Nav\0"u8);
    }
    public static JObj Read(byte[] b)
    {
        bool big = b.Length >= 6 && b[4] == 0 && b[5] == 0;
        uint ver = Bin.U32(b, 4, big);
        int n = (int)Bin.U32(b, 8, big);
        List<object?> meshes = [];
        for (int i = 0; i < n; i++)
        {
            int q = 12 + (i * 0x28);
            int nv = (int)Bin.U32(b, q, big);
            int np = (int)Bin.U32(b, q + 4, big);
            int ne = (int)Bin.U32(b, q + 8, big);
            List<object?> box = J.Doubles(Bin.F32s(b, q + 12, 6, big));
            int off = (int)Bin.U32(b, q + 36, big);
            List<object?> verts = [];
            for (int k = 0; k < nv; k++)
            {
                verts.Add(J.Doubles(Bin.F32s(b, off + (k * 12), 3, big)));
            }
            int o = off + (nv * 12);
            List<object?> polys = [];
            for (int k = 0; k < np; k++)
            {
                int p = o + (k * 20);
                polys.Add(new JObj
                {
                    ["v"] = J.Longs(new long[] { Bin.U16(b, p, big), Bin.U16(b, p + 2, big), Bin.U16(b, p + 4, big) }),
                    ["junk"] = (long)Bin.U16(b, p + 6, big),
                    ["first_edge"] = (long)Bin.U32(b, p + 8, big),
                    ["edges"] = (long)Bin.U32(b, p + 12, big),
                    ["flags"] = (long)Bin.U32(b, p + 16, big),
                });
            }
            o += np * 20;
            List<object?> edges = [];
            for (int k = 0; k < ne; k++)
            {
                edges.Add(J.Longs(new long[] { Bin.U16(b, o + (k * 4), big), Bin.U16(b, o + (k * 4) + 2, big) }));
            }
            meshes.Add(new JObj { ["bbox"] = box, ["vertices"] = verts, ["polygons"] = polys, ["edges"] = edges });
        }
        return new JObj { ["endian"] = big ? "big" : "little", ["version"] = (long)ver, ["meshes"] = meshes };
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        List<object?> ms = J.A(doc["meshes"]);
        ByteWriter head = new();
        head.Add("Nav\0"u8);
        head.U32(J.U(doc["version"]), big);
        head.U32((uint)ms.Count, big);
        ByteWriter data = new();
        int baseOff = 12 + (0x28 * ms.Count);
        foreach (object? mo in ms)
        {
            JObj m = J.O(mo);
            List<object?> verts = J.A(m["vertices"]);
            List<object?> polys = J.A(m["polygons"]);
            List<object?> edges = J.A(m["edges"]);
            head.U32((uint)verts.Count, big);
            head.U32((uint)polys.Count, big);
            head.U32((uint)edges.Count, big);
            List<object?> box = J.A(m["bbox"]);
            if (box.Count != 6)
            {
                throw new StructException("pack expected 10 items for packing");
            }
            foreach (object? x in box)
            {
                head.F32(J.D(x), big);
            }
            head.U32((uint)(baseOff + data.Length), big);
            foreach (object? v in verts)
            {
                List<object?> xyz = J.A(v);
                if (xyz.Count != 3)
                {
                    throw new StructException("pack expected 3 items for packing");
                }
                foreach (object? x in xyz)
                {
                    data.F32(J.D(x), big);
                }
            }
            foreach (object? po in polys)
            {
                JObj p = J.O(po);
                List<object?> vv = J.A(p["v"]);
                if (vv.Count != 3)
                {
                    throw new StructException("pack expected 7 items for packing");
                }
                foreach (object? x in vv)
                {
                    data.U16(J.U16(x), big);
                }
                data.U16(J.U16(p["junk"]), big);
                data.U32(J.U(p["first_edge"]), big);
                data.U32(J.U(p["edges"]), big);
                data.U32(J.U(p["flags"]), big);
            }
            foreach (object? e in edges)
            {
                List<object?> pair = J.A(e);
                if (pair.Count != 2)
                {
                    throw new StructException("pack expected 2 items for packing");
                }
                data.U16(J.U16(pair[0]), big);
                data.U16(J.U16(pair[1]), big);
            }
        }
        head.Add(data.Span);
        return head.ToArray();
    }
    public static string ToObj(JObj doc)
    {
        StringBuilder sb = new();
        int baseIndex = 1;
        List<object?> meshes = J.A(doc["meshes"]);
        List<string> lines = [];
        for (int i = 0; i < meshes.Count; i++)
        {
            JObj m = J.O(meshes[i]);
            lines.Add($"o navmesh_{i}");
            List<object?> verts = J.A(m["vertices"]);
            foreach (object? v in verts)
            {
                List<object?> xyz = J.A(v);
                lines.Add($"v {Py.Repr(J.D(xyz[0]))} {Py.Repr(J.D(xyz[1]))} {Py.Repr(J.D(xyz[2]))}");
            }
            foreach (object? po in J.A(m["polygons"]))
            {
                List<object?> pv = J.A(J.O(po)["v"]);
                lines.Add($"f {J.L(pv[0]) + baseIndex} {J.L(pv[1]) + baseIndex} {J.L(pv[2]) + baseIndex}");
            }
            baseIndex += verts.Count;
        }
        sb.AppendJoin('\n', lines);
        sb.Append('\n');
        return sb.ToString();
    }
}
internal static class Col
{
    public static bool IsCol(ReadOnlySpan<byte> b)
    {
        return b.Length >= 16 && b[..4].SequenceEqual("PCOL"u8);
    }
    private static object? F(double v)
    {
        if (double.IsFinite(v))
        {
            return v;
        }
        byte[] bits = new byte[4];
        Bin.PutU32(bits, 0, BitConverter.SingleToUInt32Bits((float)v), true);
        return new JObj { ["bits"] = Bin.Hex(bits) };
    }
    private static byte[] Fb(object? x)
    {
        if (x is JObj o)
        {
            return Bin.FromHex(J.S(o["bits"]));
        }
        byte[] bits = new byte[4];
        Bin.PutF32(bits, 0, J.D(x), true);
        return bits;
    }
    private static List<object?> Floats(byte[] b, int o, int n, bool big)
    {
        List<object?> v = new(n);
        for (int i = 0; i < n; i++)
        {
            v.Add(F(BitConverter.UInt32BitsToSingle(Bin.U32(b, o + (4 * i), big))));
        }
        return v;
    }
    private static void PackFloats(ByteWriter o, List<object?> vals, bool big)
    {
        foreach (object? v in vals)
        {
            o.U32(Bin.U32(Fb(v), 0, true), big);
        }
    }
    public static JObj Read(byte[] b)
    {
        bool big = b.Length >= 6 && b[4] == 0 && b[5] == 0;
        uint ver = Bin.U32(b, 4, big);
        int n = (int)Bin.U32(b, 8, big);
        int objs = (int)Bin.U32(b, 12, big);
        List<object?> objects = [];
        JObj doc = new() { ["endian"] = big ? "big" : "little", ["version"] = (long)ver, ["mesh"] = null, ["objects"] = objects };
        if (objs > 0x10)
        {
            const int B = 0x10;
            uint[] h = Bin.U32s(b, B + 4, 12, big);
            int nv = (int)h[1];
            int nt = (int)h[2];
            int ns = (int)h[3];
            int tsize = (int)h[4];
            int ov = (int)h[5];
            int onrm = (int)h[6];
            int osurf = (int)h[7];
            int otsf = (int)h[8];
            int otri = (int)h[9];
            int otree = (int)h[10];
            JObj mesh = new()
            {
                ["version"] = (long)h[0],
                ["hash"] = Py.Hex8(h[11]),
                ["surfaces"] = J.List(Bin.U32s(b, B + osurf, ns, big).Select(x => (object?)Py.Hex8(x))),
                ["vertices"] = J.List(Enumerable.Range(0, nv).Select(i => (object?)Floats(b, B + ov + (12 * i), 3, big))),
                ["triangles"] = J.List(Enumerable.Range(0, nt).Select(i => (object?)J.Longs(new long[]
                {
                    Bin.U16(b, B + otri + (6 * i), big), Bin.U16(b, B + otri + (6 * i) + 2, big), Bin.U16(b, B + otri + (6 * i) + 4, big),
                }))),
                ["triangle_surface"] = J.Longs(Bin.Slice(b, B + otsf, B + otsf + nt)),
                ["corner_normals"] = J.List(Enumerable.Range(0, nt).Select(i => (object?)Floats(b, B + onrm + (36 * i), 9, big))),
                ["tree"] = Convert.ToBase64String(Bin.Slice(b, B + otree, B + otree + tsize)),
            };
            doc["mesh"] = mesh;
        }
        for (int i = 0; i < n; i++)
        {
            int q = objs + (i * 0x40);
            uint[] w = Bin.U32s(b, q, 14, big);
            int nsh = (int)w[0];
            int osh = (int)w[12];
            int oprop = (int)w[13];
            List<object?> shapes = [];
            for (int s = 0; s < nsh; s++)
            {
                int o = osh + (s * 0x30);
                shapes.Add(new JObj { ["type"] = (long)Bin.U32(b, o, big), ["params"] = Floats(b, o + 4, 11, big) });
            }
            List<object?> lists = [];
            for (int k = 0; k < 5; k++)
            {
                lists.Add(J.Longs(Bin.U32s(b, (int)w[2 + k], (int)w[7 + k], big)));
            }
            List<object?> props = Floats(b, oprop, 3, big);
            props.Add(Py.Hex8(Bin.U32(b, oprop + 12, big)));
            objects.Add(new JObj
            {
                ["zero"] = (long)w[1],
                ["flags"] = Bin.Hex(Bin.Slice(b, q + 0x38, q + 0x3C)),
                ["tail"] = (long)Bin.U32(b, q + 0x3C, big),
                ["shapes"] = shapes,
                ["lists"] = lists,
                ["properties"] = props,
            });
        }
        return doc;
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        ByteWriter output = new();
        output.Zeros(16);
        if (doc["mesh"] is JObj m && m.Count > 0)
        {
            List<object?> surfaces = J.A(m["surfaces"]);
            List<object?> vertices = J.A(m["vertices"]);
            List<object?> triangles = J.A(m["triangles"]);
            int ns = surfaces.Count;
            int nv = vertices.Count;
            int nt = triangles.Count;
            byte[] tree = Convert.FromBase64String(J.S(m["tree"]));
            int osurf = 0x40;
            int ov = Bin.Align(osurf + (4 * ns), 16);
            int onrm = Bin.Align(ov + (12 * nv), 16);
            int otri = Bin.Align(onrm + (36 * nt), 16);
            int otsf = Bin.Align(otri + (6 * nt), 16);
            int otree = Bin.Align(otsf + nt, 16);
            byte[] body = new byte[otree + tree.Length];
            "COL "u8.CopyTo(body);
            uint[] head = [J.U(m["version"]), (uint)nv, (uint)nt, (uint)ns, (uint)tree.Length, (uint)ov, (uint)onrm, (uint)osurf, (uint)otsf, (uint)otri, (uint)otree, Py.ParseHex(J.S(m["hash"]))];
            for (int k = 0; k < 12; k++)
            {
                Bin.PutU32(body, 4 + (4 * k), head[k], big);
            }
            for (int k = 0; k < ns; k++)
            {
                Bin.PutU32(body, osurf + (4 * k), Py.ParseHex(J.S(surfaces[k])), big);
            }
            ByteWriter vb = new();
            foreach (object? v in vertices)
            {
                PackFloats(vb, J.A(v), big);
            }
            Place(body, ov, vb, 12 * nv);
            ByteWriter nb = new();
            foreach (object? v in J.A(m["corner_normals"]))
            {
                PackFloats(nb, J.A(v), big);
            }
            Place(body, onrm, nb, 36 * nt);
            ByteWriter tb = new();
            foreach (object? t in triangles)
            {
                List<object?> tri = J.A(t);
                if (tri.Count != 3)
                {
                    throw new StructException("pack expected 3 items for packing");
                }
                foreach (object? x in tri)
                {
                    tb.U16(J.U16(x), big);
                }
            }
            Place(body, otri, tb, 6 * nt);
            List<object?> ts = J.A(m["triangle_surface"]);
            ByteWriter sb = new();
            foreach (object? x in ts)
            {
                sb.Add(J.U8(x));
            }
            Place(body, otsf, sb, nt);
            tree.CopyTo(body, otree);
            output.Add(body);
        }
        int objs = output.Length;
        List<object?> objects = J.A(doc["objects"]);
        int n = objects.Count;
        output.Zeros(0x40 * n);
        List<int> shapeOff = [];
        foreach (object? oo in objects)
        {
            JObj o = J.O(oo);
            shapeOff.Add(output.Length);
            foreach (object? so in J.A(o["shapes"]))
            {
                JObj s = J.O(so);
                output.U32(J.U(s["type"]), big);
                PackFloats(output, J.A(s["params"]), big);
            }
        }
        int[,] listOff = new int[n, 5];
        for (int k = 0; k < 5; k++)
        {
            for (int i = 0; i < n; i++)
            {
                listOff[i, k] = output.Length;
                foreach (object? x in J.A(J.A(J.O(objects[i])["lists"])[k]))
                {
                    output.U32(J.U(x), big);
                }
            }
            output.Pad(16);
        }
        List<int> propOff = [];
        foreach (object? oo in objects)
        {
            JObj o = J.O(oo);
            propOff.Add(output.Length);
            List<object?> p = J.A(o["properties"]);
            PackFloats(output, p.GetRange(0, 3), big);
            output.U32(Py.ParseHex(J.S(p[3])), big);
        }
        for (int i = 0; i < n; i++)
        {
            JObj o = J.O(objects[i]);
            int q = objs + (i * 0x40);
            List<object?> lists = J.A(o["lists"]);
            output.PatchU32(q, (uint)J.A(o["shapes"]).Count, big);
            output.PatchU32(q + 4, J.U(o["zero"]), big);
            for (int k = 0; k < 5; k++)
            {
                output.PatchU32(q + 8 + (4 * k), (uint)listOff[i, k], big);
                output.PatchU32(q + 28 + (4 * k), (uint)J.A(lists[k]).Count, big);
            }
            output.PatchU32(q + 48, (uint)shapeOff[i], big);
            output.PatchU32(q + 0x34, (uint)propOff[i], big);
            byte[] flags = Bin.FromHex(J.S(o["flags"]));
            output.Write(q + 0x38, flags.AsSpan(0, Math.Min(4, flags.Length)));
            output.PatchU32(q + 0x3C, J.U(o["tail"]), big);
        }
        output.Write(0, "PCOL"u8);
        output.PatchU32(4, J.U(doc["version"]), big);
        output.PatchU32(8, (uint)n, big);
        output.PatchU32(12, (uint)objs, big);
        return output.ToArray();
    }
    private static void Place(byte[] body, int at, ByteWriter data, int size)
    {
        if (data.Length != size)
        {
            throw new StructException($"a block of {data.Length} bytes in a slot of {size}");
        }
        data.Span.CopyTo(body.AsSpan(at));
    }
    public static string ToObj(JObj doc)
    {
        if (doc["mesh"] is not JObj m || m.Count == 0)
        {
            return string.Empty;
        }
        List<string> lines = [];
        foreach (object? v in J.A(m["vertices"]))
        {
            List<object?> xyz = J.A(v);
            lines.Add($"v {Repr(xyz[0])} {Repr(xyz[1])} {Repr(xyz[2])}");
        }
        List<object?> surfaces = J.A(m["surfaces"]);
        List<object?> tris = J.A(m["triangles"]);
        List<object?> ts = J.A(m["triangle_surface"]);
        for (int s = 0; s < surfaces.Count; s++)
        {
            List<string> faces = [];
            for (int i = 0; i < Math.Min(tris.Count, ts.Count); i++)
            {
                if (J.L(ts[i]) == s)
                {
                    List<object?> t = J.A(tris[i]);
                    faces.Add($"f {J.L(t[0]) + 1} {J.L(t[1]) + 1} {J.L(t[2]) + 1}");
                }
            }
            if (faces.Count > 0)
            {
                lines.Add($"g surface_{J.S(surfaces[s])}");
                lines.AddRange(faces);
            }
        }
        return string.Join('\n', lines) + "\n";
    }
    private static string Repr(object? v)
    {
        return v is JObj o ? PyDictRepr(o) : Py.Repr(J.D(v));
    }
    private static string PyDictRepr(JObj o)
    {
        return "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
    }
}
internal static class Nog
{
    public static bool IsNog(ReadOnlySpan<byte> b)
    {
        return b.Length >= 16 && b[..4].SequenceEqual("NOG "u8);
    }
    public static JObj Read(byte[] b)
    {
        uint zero = Bin.U32(b, 4, false);
        uint size = Bin.U32(b, 8, false);
        uint level = Bin.U32(b, 12, false);
        if (!IsNog(b) || zero != 0 || size != b.Length)
        {
            throw new InvalidDataException("not a NOG");
        }
        List<string> parts = [.. Bin.Latin1(b.AsSpan(16)).Split('\0')];
        parts.RemoveAt(parts.Count - 1);
        return new JObj { ["level_hash"] = (long)level, ["names"] = J.List(parts.Select(p => (object?)p)) };
    }
    public static byte[] Write(JObj doc)
    {
        ByteWriter body = new();
        foreach (object? s in J.A(doc["names"]))
        {
            body.Add(Bin.Latin1(J.S(s)));
            body.Add(0);
        }
        ByteWriter o = new();
        o.Add("NOG "u8);
        o.U32(0, false);
        o.U32((uint)(16 + body.Length), false);
        o.U32(J.U(doc["level_hash"]), false);
        o.Add(body.Span);
        return o.ToArray();
    }
    public static IEnumerable<string> Strings(ReadOnlySpan<byte> b)
    {
        return Bin.Latin1(b[Math.Min(16, b.Length)..]).Split('\0').Where(s => s.Length > 0);
    }
}
internal sealed record U8Node(int Type, string Name, uint A, uint C, string Path);
internal static class U8
{
    public const uint Magic = 0x55AA382D;
    public static bool IsU8(ReadOnlySpan<byte> b)
    {
        return b.Length >= 0x20 && Bin.U32(b, 0, true) == Magic;
    }
    public static (List<U8Node> Nodes, uint Data, uint Size) Read(byte[] b)
    {
        if (Bin.U32(b, 0, true) != Magic)
        {
            throw new InvalidDataException("not U8");
        }
        int root = (int)Bin.U32(b, 4, true);
        uint size = Bin.U32(b, 8, true);
        uint data = Bin.U32(b, 12, true);
        int n = (int)Bin.U32(b, root + 8, true);
        if (n < 0 || root + (n * 12L) > b.Length)
        {
            throw new InvalidDataException($"U8 root at 0x{root:X} declares {n} nodes, more than the {b.Length}-byte file holds");
        }
        int strings = root + (n * 12);
        List<(int Type, string Name, uint A, uint C)> raw = [];
        for (int i = 0; i < n; i++)
        {
            uint t = Bin.U32(b, root + (i * 12), true);
            uint a = Bin.U32(b, root + (i * 12) + 4, true);
            uint c = Bin.U32(b, root + (i * 12) + 8, true);
            raw.Add(((int)(t >> 24), Bin.CString(b, strings + (int)(t & 0xFFFFFF)), a, c));
        }
        List<U8Node> nodes = [];
        List<(int I, long End, string P)> stack = [(0, n, string.Empty)];
        for (int i = 0; i < raw.Count; i++)
        {
            while (stack.Count > 0 && i >= stack[^1].End)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            string bse = stack.Count > 0 ? stack[^1].P : string.Empty;
            string p = i != 0 ? $"{bse}/{raw[i].Name}" : string.Empty;
            nodes.Add(new U8Node(raw[i].Type, raw[i].Name, raw[i].A, raw[i].C, p.TrimStart('/')));
            if (raw[i].Type == 1 && i != 0)
            {
                stack.Add((i, raw[i].C, p));
            }
        }
        return (nodes, data, size);
    }
    public static JObj Manifest(byte[] b)
    {
        (List<U8Node> nodes, uint data, uint size) = Read(b);
        long tailStart = Math.Max(data, nodes.Where(nd => nd.Type == 0).Select(nd => (long)nd.A + nd.C).DefaultIfEmpty(data).Max());
        return new JObj
        {
            ["nodes"] = J.List(nodes.Select(nd => (object?)new JObj
            {
                ["type"] = (long)nd.Type,
                ["name"] = nd.Name,
                ["a"] = (long)nd.A,
                ["c"] = (long)nd.C,
                ["path"] = nd.Path,
            })),
            ["data_offset"] = (long)data,
            ["header_size"] = (long)size,
            ["tail"] = Bin.Hex(Bin.Slice(b, (int)Math.Min(tailStart, int.MaxValue), b.Length)),
        };
    }
    public static byte[] Pack(JObj man, Func<string, byte[]> read)
    {
        List<object?> nodes = J.A(man["nodes"]);
        ByteWriter names = new();
        List<int> offs = [];
        foreach (object? no in nodes)
        {
            offs.Add(names.Length);
            names.Add(Bin.Latin1(J.S(J.O(no)["name"])));
            names.Add(0);
        }
        ByteWriter table = new();
        for (int i = 0; i < nodes.Count; i++)
        {
            JObj nd = J.O(nodes[i]);
            table.U32((J.U(nd["type"]) << 24) | (uint)offs[i], true);
            table.U32(J.U(nd["a"]), true);
            table.U32(J.U(nd["c"]), true);
        }
        int data = J.I(man["data_offset"]);
        ByteWriter buf = new();
        buf.U32(Magic, true);
        buf.U32(0x20, true);
        buf.U32(J.U(man["header_size"]), true);
        buf.U32((uint)data, true);
        buf.Zeros(16);
        buf.Add(table.Span);
        buf.Add(names.Span);
        buf.Zeros(data - buf.Length);
        foreach (object? no in nodes)
        {
            JObj nd = J.O(no);
            if (J.L(nd["type"]) != 0)
            {
                continue;
            }
            byte[] blob = read(J.S(nd["path"]));
            if (blob.Length != J.L(nd["c"]))
            {
                throw new InvalidDataException($"{nd["path"]}: size changed; in-place repack only");
            }
            int a = J.I(nd["a"]);
            if (a >= buf.Length)
            {
                buf.Zeros(a - buf.Length);
            }
            buf.Write(a, blob);
        }
        buf.Add(Bin.FromHex(J.S(man["tail"])));
        return buf.ToArray();
    }
}
internal sealed record SwfTag(int Code, int Offset, int Length, int Start);
internal static class Swf
{
    private const int MaxInflateRatio = 1032;
    private static readonly Dictionary<int, string> s_tags = new()
    {
        [0] = "End", [1] = "ShowFrame", [2] = "DefineShape", [4] = "PlaceObject", [5] = "RemoveObject", [6] = "DefineBits",
        [8] = "JPEGTables", [9] = "SetBackgroundColor", [10] = "DefineFont", [11] = "DefineText", [12] = "DoAction",
        [13] = "DefineFontInfo", [14] = "DefineSound", [15] = "StartSound", [18] = "SoundStreamHead", [19] = "SoundStreamBlock",
        [20] = "DefineBitsLossless", [21] = "DefineBitsJPEG2", [22] = "DefineShape2", [24] = "Protect", [26] = "PlaceObject2",
        [28] = "RemoveObject2", [32] = "DefineShape3", [33] = "DefineText2", [34] = "DefineButton2", [35] = "DefineBitsJPEG3",
        [36] = "DefineBitsLossless2", [37] = "DefineEditText", [39] = "DefineSprite", [43] = "FrameLabel",
        [45] = "SoundStreamHead2", [46] = "DefineMorphShape", [48] = "DefineFont2", [56] = "ExportAssets", [57] = "ImportAssets",
        [58] = "EnableDebugger", [59] = "DoInitAction", [60] = "DefineVideoStream", [62] = "DefineFontInfo2",
        [64] = "EnableDebugger2", [65] = "ScriptLimits", [66] = "SetTabIndex", [69] = "FileAttributes", [70] = "PlaceObject3",
        [71] = "ImportAssets2", [73] = "DefineFontAlignZones", [74] = "CSMTextSettings", [75] = "DefineFont3",
        [76] = "SymbolClass", [77] = "Metadata", [78] = "DefineScalingGrid", [82] = "DoABC", [83] = "DefineShape4",
        [84] = "DefineMorphShape2", [86] = "DefineSceneAndFrameLabelData", [87] = "DefineBinaryData",
        [88] = "DefineFontName", [1000] = "GFx_ExporterInfo", [1001] = "GFx_DefineExternalImage",
        [1002] = "GFx_FontTextureInfo", [1003] = "GFx_DefineExternalGradient", [1004] = "GFx_DefineGradientMap",
        [1005] = "GFx_DefineCompactedFont", [1006] = "GFx_DefineExternalSound", [1007] = "GFx_DefineExternalStreamSound",
        [1008] = "GFx_DefineSubImage", [1009] = "GFx_DefineExternalImage2",
    };
    public static string TagName(int code)
    {
        return s_tags.TryGetValue(code, out string? n) ? n : $"tag{code}";
    }
    public static bool IsSwf(ReadOnlySpan<byte> b)
    {
        return b.Length >= 8 && (b[..3].SequenceEqual("CWS"u8) || b[..3].SequenceEqual("FWS"u8));
    }
    public static byte[] Decompress(byte[] b)
    {
        if (b.AsSpan(0, 3).SequenceEqual("FWS"u8))
        {
            return b;
        }
        if (!b.AsSpan(0, 3).SequenceEqual("CWS"u8))
        {
            throw new InvalidDataException("not a SWF");
        }
        using MemoryStream input = new(b, 8, b.Length - 8);
        using ZLibStream z = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        output.Write("FWS"u8);
        output.Write(b, 3, 5);
        long limit = Math.Min(Array.MaxLength, 8 + ((b.Length - 8L) * MaxInflateRatio));
        CopyAtMost(z, output, limit);
        return output.Length > limit
            ? throw new InvalidDataException($"a {b.Length}-byte compressed SWF inflates past the {limit} bytes zlib can produce from it")
            : output.ToArray();
    }
    public static byte[] Compress(byte[] fws)
    {
        using MemoryStream output = new();
        output.Write("CWS"u8);
        output.Write(fws, 3, 5);
        using (ZLibStream z = new(output, CompressionLevel.Optimal, true))
        {
            z.Write(fws, 8, fws.Length - 8);
        }
        return output.ToArray();
    }
    public static (double Rate, int Frames, List<SwfTag> Tags) Tags(byte[] fws)
    {
        int nbits = Bin.U8(fws, 8) >> 3;
        int o = 8 + (((5 + (4 * nbits)) + 7) / 8);
        int rate = Bin.U16(fws, o, false);
        int frames = Bin.U16(fws, o + 2, false);
        o += 4;
        List<SwfTag> tags = [];
        while (o < fws.Length)
        {
            int start = o;
            int codeLen = Bin.U16(fws, o, false);
            int code = codeLen >> 6;
            int len = codeLen & 0x3F;
            o += 2;
            if (len == 0x3F)
            {
                len = (int)Bin.U32(fws, o, false);
                o += 4;
            }
            if (len < 0)
            {
                throw new InvalidDataException($"SWF tag at 0x{start:X} declares a length of {(uint)len} bytes");
            }
            tags.Add(new SwfTag(code, o, len, start));
            o += len;
            if (code == 0)
            {
                break;
            }
        }
        return (rate / 256.0, frames, tags);
    }
    private static (string, int) CStr(ReadOnlySpan<byte> b, int o)
    {
        int e = b[o..].IndexOf((byte)0);
        if (e < 0)
        {
            throw new InvalidDataException("subsection not found");
        }
        return (Bin.Latin1(b.Slice(o, e)), o + e + 1);
    }
    public static JObj Info(byte[] fws)
    {
        (double rate, int frames, List<SwfTag> ts) = Tags(fws);
        List<object?> tags = [];
        JObj exports = [];
        List<object?> external = [];
        List<object?> labels = [];
        List<object?> fonts = [];
        JObj info = new()
        {
            ["version"] = (long)fws[3],
            ["frame_rate"] = rate,
            ["frames"] = (long)frames,
            ["tags"] = tags,
            ["exports"] = exports,
            ["external_images"] = external,
            ["labels"] = labels,
            ["fonts"] = fonts,
        };
        foreach (SwfTag t in ts)
        {
            ReadOnlySpan<byte> body = Bin.Slice(fws, t.Offset, t.Offset + t.Length);
            tags.Add(new JObj { ["code"] = (long)t.Code, ["name"] = TagName(t.Code), ["length"] = (long)t.Length });
            switch (t.Code)
            {
                case 56:
                {
                    int n = Bin.U16(body, 0, false);
                    int p = 2;
                    for (int i = 0; i < n; i++)
                    {
                        int cid = Bin.U16(body, p, false);
                        (string name, int next) = CStr(body, p + 2);
                        p = next;
                        exports[name] = (long)cid;
                    }
                    break;
                }
                case 43:
                    labels.Add(CStr(body, 0).Item1);
                    break;
                case 1001 or 1009:
                {
                    long cid = t.Code == 1009 ? Bin.U32(body, 0, false) : Bin.U16(body, 0, false);
                    int p = t.Code == 1009 ? 4 : 2;
                    int fmt = Bin.U16(body, p, false);
                    int w = Bin.U16(body, p + 2, false);
                    int h = Bin.U16(body, p + 4, false);
                    p += 6;
                    int n = Bin.U8(body, p);
                    string name = Bin.Latin1(Bin.Slice(body, p + 1, p + 1 + n));
                    p += 1 + n;
                    string fn = p < body.Length ? Bin.Latin1(Bin.Slice(body, p + 1, p + 1 + body[p])) : string.Empty;
                    external.Add(new JObj
                    {
                        ["id"] = cid,
                        ["format"] = (long)fmt,
                        ["width"] = (long)w,
                        ["height"] = (long)h,
                        ["name"] = name,
                        ["file"] = fn,
                    });
                    break;
                }
                case 48 or 75:
                {
                    int cid = Bin.U16(body, 0, false);
                    int nlen = Bin.U8(body, 4);
                    fonts.Add(new JObj { ["id"] = (long)cid, ["name"] = Bin.Latin1(Bin.Slice(body, 5, 5 + nlen)) });
                    break;
                }
                default:
                    break;
            }
        }
        return info;
    }
    public static bool IsImage(SwfTag t)
    {
        return t.Code is 20 or 21 or 35 or 36;
    }
    public static int ImageId(SwfTag t, byte[] fws)
    {
        return Bin.U16(fws, t.Offset, false);
    }
    public static byte[] ReplaceImage(byte[] fws, SwfTag t, Omega.Plugins.ImageData image, Func<Omega.Plugins.ImageData, byte[]> jpeg)
    {
        ByteWriter body = new(image.Rgba.Length + 64);
        body.U16((uint)ImageId(t, fws), false);
        if (t.Code is 20 or 36)
        {
            byte[] argb = new byte[image.Width * image.Height * 4];
            for (int i = 0; i < argb.Length; i += 4)
            {
                argb[i] = t.Code == 36 ? image.Rgba[i + 3] : (byte)0;
                argb[i + 1] = image.Rgba[i];
                argb[i + 2] = image.Rgba[i + 1];
                argb[i + 3] = image.Rgba[i + 2];
            }
            body.Add(5);
            body.U16((uint)image.Width, false);
            body.U16((uint)image.Height, false);
            body.Add(ZlibCompress(argb));
        }
        else
        {
            byte[] opaque = (byte[])image.Rgba.Clone();
            byte[] alpha = new byte[image.Width * image.Height];
            for (int i = 0; i < alpha.Length; i++)
            {
                alpha[i] = opaque[(i * 4) + 3];
                if (alpha[i] == 0)
                {
                    opaque.AsSpan(i * 4, 3).Clear();
                }
                opaque[(i * 4) + 3] = 255;
            }
            byte[] jpg = jpeg(Omega.Plugins.ImageData.Create(image.Name, image.Width, image.Height, opaque, "JPEG"));
            if (t.Code == 35)
            {
                body.U32((uint)jpg.Length, false);
                body.Add(jpg);
                body.Add(ZlibCompress(alpha));
            }
            else
            {
                body.Add(jpg);
            }
        }
        ByteWriter o = new(fws.Length + body.Length);
        o.Add(fws.AsSpan(0, t.Start));
        o.U16((uint)((t.Code << 6) | 0x3F), false);
        o.U32((uint)body.Length, false);
        o.Add(body.Span);
        o.Add(fws.AsSpan(t.Offset + t.Length));
        o.PatchU32(4, (uint)o.Length, false);
        return o.ToArray();
    }
    private static byte[] ZlibCompress(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream z = new(output, CompressionLevel.Optimal, true))
        {
            z.Write(data);
        }
        return output.ToArray();
    }
    public static byte[] ZlibDecompress(ReadOnlySpan<byte> data, long limit)
    {
        using MemoryStream input = new(data.ToArray());
        using ZLibStream z = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        CopyAtMost(z, output, limit);
        return output.ToArray();
    }
    private static void CopyAtMost(Stream from, MemoryStream to, long limit)
    {
        byte[] buffer = new byte[81920];
        int read;
        while (to.Length <= limit && (read = from.Read(buffer)) > 0)
        {
            to.Write(buffer, 0, read);
        }
    }
    public static (int Id, string Kind, Func<Func<byte[], Omega.Plugins.ImageData>, Omega.Plugins.ImageData> Decode)? Image(SwfTag t, byte[] fws)
    {
        if (t.Code is not (20 or 21 or 35 or 36))
        {
            return null;
        }
        byte[] body = fws.AsSpan(t.Offset, Math.Min(t.Length, fws.Length - t.Offset)).ToArray();
        int cid = Bin.U16(body, 0, false);
        string name = cid.ToString(CultureInfo.InvariantCulture);
        switch (t.Code)
        {
            case 21:
                return (cid, "jpg", load => load(body[2..]));
            case 35:
            {
                int alen = (int)Bin.U32(body, 2, false);
                byte[] jpg = Bin.Slice(body, 6, 6 + alen).ToArray();
                return (cid, "jpg", load =>
                {
                    Omega.Plugins.ImageData im = load(jpg);
                    byte[] alpha = body.Length > 6 + alen ? ZlibDecompress(body.AsSpan(6 + alen), (long)im.Width * im.Height) : [];
                    if (alpha.Length != im.Width * im.Height)
                    {
                        return im;
                    }
                    byte[] rgba = (byte[])im.Rgba.Clone();
                    for (int i = 0; i < alpha.Length; i++)
                    {
                        rgba[(i * 4) + 3] = alpha[i];
                    }
                    return Omega.Plugins.ImageData.Create(name, im.Width, im.Height, rgba, "JPEG + alpha");
                });
            }
            case 20 or 36:
            {
                bool hasAlpha = t.Code == 36;
                int fmt = Bin.U8(body, 2);
                int w = Bin.U16(body, 3, false);
                int h = Bin.U16(body, 5, false);
                if (fmt == 3)
                {
                    int ncol = body[7] + 1;
                    return (cid, "png", _ =>
                    {
                        int csz = hasAlpha ? 4 : 3;
                        int pitch = (w + 3) & ~3;
                        if ((long)w * h * 4 > Array.MaxLength)
                        {
                            throw new InvalidDataException($"lossless image {cid} is {w}x{h}, larger than one image can hold");
                        }
                        long need = (ncol * (long)csz) + ((long)Math.Max(0, h - 1) * pitch) + w;
                        byte[] raw = ZlibDecompress(body.AsSpan(8), need);
                        if (w > 0 && h > 0 && raw.Length < need)
                        {
                            throw new InvalidDataException($"lossless image {cid} holds {raw.Length} bytes, a {w}x{h} palette image needs {need}");
                        }
                        byte[] rgba = new byte[w * h * 4];
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                int i = raw[(ncol * csz) + (y * pitch) + x];
                                for (int c = 0; c < 3; c++)
                                {
                                    rgba[(((y * w) + x) * 4) + c] = raw[(i * csz) + c];
                                }
                                rgba[(((y * w) + x) * 4) + 3] = hasAlpha ? raw[(i * csz) + 3] : (byte)255;
                            }
                        }
                        return Omega.Plugins.ImageData.Create(name, w, h, rgba, "Lossless palette");
                    });
                }
                if (fmt == 5)
                {
                    return (cid, "png", _ =>
                    {
                        long need = (long)w * h * 4;
                        if (need > Array.MaxLength)
                        {
                            throw new InvalidDataException($"lossless image {cid} is {w}x{h}, larger than one image can hold");
                        }
                        byte[] raw = ZlibDecompress(body.AsSpan(7), need);
                        if (raw.Length < need)
                        {
                            throw new InvalidDataException($"lossless image {cid} holds {raw.Length} bytes, a {w}x{h} ARGB image needs {need}");
                        }
                        byte[] rgba = new byte[w * h * 4];
                        for (int i = 0; i < w * h; i++)
                        {
                            rgba[i * 4] = raw[(i * 4) + 1];
                            rgba[(i * 4) + 1] = raw[(i * 4) + 2];
                            rgba[(i * 4) + 2] = raw[(i * 4) + 3];
                            rgba[(i * 4) + 3] = hasAlpha ? raw[i * 4] : (byte)255;
                        }
                        return Omega.Plugins.ImageData.Create(name, w, h, rgba, "Lossless ARGB");
                    });
                }
                return null;
            }
            default:
                return null;
        }
    }
}
