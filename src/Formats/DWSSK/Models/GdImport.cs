using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
namespace DWSSK.Formats.Models;
internal sealed class GltfPrim
{
    public required string Material { get; init; }
    public required Dictionary<string, double[,]> Attributes { get; init; }
    public required int[,] Triangles { get; init; }
    public long Zone { get; init; }
    public required List<string> Joints { get; init; }
}
internal static partial class GdImport
{
    [GeneratedRegex(@"zone_?(\d+)")]
    private static partial Regex ZonePattern();
    [GeneratedRegex(@"\.\d{3}$")]
    private static partial Regex BlenderSuffix();
    private static string Strip(string name)
    {
        return BlenderSuffix().Replace(name, string.Empty, 1);
    }
    public static List<GltfPrim> ReadGltf(JsonElement doc, IReadOnlyList<byte[]> bins)
    {
        JsonElement[] accessors = Arr(doc, "accessors");
        JsonElement[] views = Arr(doc, "bufferViews");
        JsonElement[] nodes = Arr(doc, "nodes");
        JsonElement[] meshes = Arr(doc, "meshes");
        JsonElement[] materials = Arr(doc, "materials");
        JsonElement[] skins = Arr(doc, "skins");
        double[,] Acc(int i)
        {
            JsonElement a = accessors[i];
            JsonElement v = views[a.GetProperty("bufferView").GetInt32()];
            int ctype = a.GetProperty("componentType").GetInt32();
            (int size, double max) = ctype switch
            {
                5120 => (1, sbyte.MaxValue),
                5121 => (1, byte.MaxValue),
                5122 => (2, short.MaxValue),
                5123 => (2, ushort.MaxValue),
                5125 => (4, uint.MaxValue),
                5126 => (4, double.NaN),
                _ => throw new KeyNotFoundException($"glTF component type {ctype}"),
            };
            int n = a.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                string t => throw new KeyNotFoundException($"glTF accessor type {t}"),
                _ => throw new KeyNotFoundException("glTF accessor type"),
            };
            int count = a.GetProperty("count").GetInt32();
            int stride = v.TryGetProperty("byteStride", out JsonElement bs) ? bs.GetInt32() : size * n;
            int start = (v.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0)
                + (a.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt32() : 0);
            byte[] buf = bins[v.GetProperty("buffer").GetInt32()];
            if (count > 0)
            {
                Bin.Need(buf, start, (stride * (count - 1)) + (size * n));
            }
            bool normalized = a.TryGetProperty("normalized", out JsonElement nz) && nz.ValueKind == JsonValueKind.True;
            double[,] x = new double[count, n];
            for (int k = 0; k < count; k++)
            {
                for (int c = 0; c < n; c++)
                {
                    int o = start + (k * stride) + (c * size);
                    double value = ctype switch
                    {
                        5120 => (sbyte)buf[o],
                        5121 => buf[o],
                        5122 => BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(o)),
                        5123 => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)),
                        _ => BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(o)),
                    };
                    if (normalized)
                    {
                        if (double.IsNaN(max))
                        {
                            throw new InvalidDataException("a normalized float accessor");
                        }
                        value /= max;
                    }
                    x[k, c] = value;
                }
            }
            return x;
        }
        static double[] Local(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out JsonElement mat))
            {
                double[] f = [.. mat.EnumerateArray().Select(e => e.GetDouble())];
                return Mat.Transpose(f);
            }
            double[] m = Mat.Identity();
            double[] q = node.TryGetProperty("rotation", out JsonElement r) ? [.. r.EnumerateArray().Select(e => e.GetDouble())] : [0, 0, 0, 1];
            (double x, double y, double z, double w) = (q[0], q[1], q[2], q[3]);
            double[] rot =
            [
                1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (z * w)), 2 * ((x * z) + (y * w)),
                2 * ((x * y) + (z * w)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (x * w)),
                2 * ((x * z) - (y * w)), 2 * ((y * z) + (x * w)), 1 - (2 * ((x * x) + (y * y))),
            ];
            double[] s = node.TryGetProperty("scale", out JsonElement sc) ? [.. sc.EnumerateArray().Select(e => e.GetDouble())] : [1, 1, 1];
            double[] t = node.TryGetProperty("translation", out JsonElement tr) ? [.. tr.EnumerateArray().Select(e => e.GetDouble())] : [0, 0, 0];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    m[(row * 4) + col] = rot[(row * 3) + col] * s[col];
                }
                m[(row * 4) + 3] = t[row];
            }
            return m;
        }
        List<GltfPrim> prims = [];
        Queue<(int Node, double[] Parent)> todo = new();
        foreach (JsonElement scene in Arr(doc, "scenes"))
        {
            foreach (JsonElement n in Arr(scene, "nodes"))
            {
                todo.Enqueue((n.GetInt32(), Mat.Identity()));
            }
        }
        while (todo.Count > 0)
        {
            (int k, double[] parent) = todo.Dequeue();
            JsonElement node = nodes[k];
            double[] world = Mat.Mul(parent, Local(node));
            foreach (JsonElement c in Arr(node, "children"))
            {
                todo.Enqueue((c.GetInt32(), world));
            }
            if (!node.TryGetProperty("mesh", out JsonElement meshRef))
            {
                continue;
            }
            foreach (JsonElement p in Arr(meshes[meshRef.GetInt32()], "primitives"))
            {
                if ((p.TryGetProperty("mode", out JsonElement mode) ? mode.GetInt32() : 4) != 4)
                {
                    throw new InvalidDataException("only triangle primitives can be imported");
                }
                Dictionary<string, double[,]> a = [];
                foreach (JsonProperty attr in p.GetProperty("attributes").EnumerateObject())
                {
                    a[attr.Name] = Acc(attr.Value.GetInt32());
                }
                double[,] pos = a["POSITION"];
                double[,] moved = new double[pos.GetLength(0), 3];
                for (int i = 0; i < pos.GetLength(0); i++)
                {
                    for (int r = 0; r < 3; r++)
                    {
                        moved[i, r] = (((pos[i, 0] * world[(r * 4) + 0]) + (pos[i, 1] * world[(r * 4) + 1])) + (pos[i, 2] * world[(r * 4) + 2])) + world[(r * 4) + 3];
                    }
                }
                a["POSITION"] = moved;
                if (a.TryGetValue("NORMAL", out double[,]? nrm))
                {
                    double[] lin = [world[0], world[1], world[2], 0, world[4], world[5], world[6], 0, world[8], world[9], world[10], 0, 0, 0, 0, 1];
                    double[] inv = Inverse3(lin);
                    double[,] nn = new double[nrm.GetLength(0), 3];
                    for (int i = 0; i < nrm.GetLength(0); i++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            nn[i, c] = ((nrm[i, 0] * inv[(0 * 3) + c]) + (nrm[i, 1] * inv[(1 * 3) + c])) + (nrm[i, 2] * inv[(2 * 3) + c]);
                        }
                        double len = Math.Max(Math.Sqrt(((nn[i, 0] * nn[i, 0]) + (nn[i, 1] * nn[i, 1])) + (nn[i, 2] * nn[i, 2])), 1e-12);
                        for (int c = 0; c < 3; c++)
                        {
                            nn[i, c] /= len;
                        }
                    }
                    a["NORMAL"] = nn;
                }
                foreach (string c in new[] { "COLOR_0", "COLOR_1" })
                {
                    if (a.TryGetValue(c, out double[,]? col))
                    {
                        double[,] full = new double[col.GetLength(0), 4];
                        for (int i = 0; i < col.GetLength(0); i++)
                        {
                            for (int k2 = 0; k2 < 4; k2++)
                            {
                                full[i, k2] = (k2 < col.GetLength(1) ? col[i, k2] : 1.0) * 255;
                            }
                        }
                        a[c] = full;
                    }
                }
                int[,] tri;
                if (p.TryGetProperty("indices", out JsonElement ind))
                {
                    double[,] raw = Acc(ind.GetInt32());
                    int total = raw.GetLength(0) * raw.GetLength(1);
                    if (total % 3 != 0)
                    {
                        throw new InvalidDataException($"an index list of {total} entries is not triangles");
                    }
                    tri = new int[total / 3, 3];
                    for (int i = 0; i < total; i++)
                    {
                        tri[i / 3, i % 3] = checked((int)raw[i / raw.GetLength(1), i % raw.GetLength(1)]);
                    }
                }
                else
                {
                    int n = moved.GetLength(0);
                    if (n % 3 != 0)
                    {
                        throw new InvalidDataException($"{n} unindexed vertices are not triangles");
                    }
                    tri = new int[n / 3, 3];
                    for (int i = 0; i < n; i++)
                    {
                        tri[i / 3, i % 3] = i;
                    }
                }
                string mat = p.TryGetProperty("material", out JsonElement mref)
                    ? (materials[mref.GetInt32()].TryGetProperty("name", out JsonElement mn) ? mn.GetString() ?? string.Empty : string.Empty)
                    : string.Empty;
                long zone;
                if (node.TryGetProperty("extras", out JsonElement extras) && extras.ValueKind == JsonValueKind.Object
                    && extras.TryGetProperty("zone", out JsonElement z) && z.ValueKind != JsonValueKind.Null)
                {
                    zone = z.TryGetInt64(out long zl) ? zl : throw new StructException("a zone in a node's extras is an integer");
                }
                else
                {
                    string name = node.TryGetProperty("name", out JsonElement nmEl) ? nmEl.GetString() ?? string.Empty : string.Empty;
                    Match m = ZonePattern().Match(name);
                    zone = m.Success ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                }
                List<string> joints = [];
                if (node.TryGetProperty("skin", out JsonElement skin))
                {
                    foreach (JsonElement j in Arr(skins[skin.GetInt32()], "joints"))
                    {
                        JsonElement jn = nodes[j.GetInt32()];
                        joints.Add(jn.TryGetProperty("name", out JsonElement jname) ? jname.GetString() ?? string.Empty : string.Empty);
                    }
                }
                prims.Add(new GltfPrim { Material = mat, Attributes = a, Triangles = tri, Zone = zone, Joints = joints });
            }
        }
        return prims;
    }
    private static double[] Inverse3(double[] m4)
    {
        double[] inv4 = Mat.Inverse(m4);
        return [inv4[0], inv4[1], inv4[2], inv4[4], inv4[5], inv4[6], inv4[8], inv4[9], inv4[10]];
    }
    private static JsonElement[] Arr(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray()] : [];
    }
    public static List<int> TrisToStrip(int[,] tri)
    {
        List<int> s = [];
        for (int i = 0; i < tri.GetLength(0); i++)
        {
            int a = tri[i, 0];
            if (s.Count > 0)
            {
                s.Add(s[^1]);
                s.Add(a);
                if (s.Count % 2 != 0)
                {
                    s.Add(a);
                }
            }
            s.Add(a);
            s.Add(tri[i, 1]);
            s.Add(tri[i, 2]);
        }
        return s;
    }
    public static (double[,] T, double[,] B) Tangents(double[,] pos, double[,] nrm, double[,] uv, int[,] tri)
    {
        int n = pos.GetLength(0);
        int m = tri.GetLength(0);
        double[,] t = new double[n, 3];
        double[,] bt = new double[n, 3];
        double[,] ft = new double[m, 3];
        double[,] fb = new double[m, 3];
        for (int i = 0; i < m; i++)
        {
            int i0 = tri[i, 0];
            int i1 = tri[i, 1];
            int i2 = tri[i, 2];
            double d1x = uv[i1, 0] - uv[i0, 0];
            double d1y = uv[i1, 1] - uv[i0, 1];
            double d2x = uv[i2, 0] - uv[i0, 0];
            double d2y = uv[i2, 1] - uv[i0, 1];
            double r = (d1x * d2y) - (d2x * d1y);
            r = Math.Abs(r) > 1e-12 ? 1 / (r == 0 ? 1 : r) : 0;
            for (int c = 0; c < 3; c++)
            {
                double e1 = pos[i1, c] - pos[i0, c];
                double e2 = pos[i2, c] - pos[i0, c];
                ft[i, c] = ((e1 * d2y) - (e2 * d1y)) * r;
                fb[i, c] = ((e2 * d1x) - (e1 * d2x)) * r;
            }
        }
        for (int k = 0; k < 3; k++)
        {
            for (int i = 0; i < m; i++)
            {
                int v = tri[i, k];
                for (int c = 0; c < 3; c++)
                {
                    t[v, c] += ft[i, c];
                    bt[v, c] += fb[i, c];
                }
            }
        }
        double[,] binormal = new double[n, 3];
        for (int i = 0; i < n; i++)
        {
            double dot = ((t[i, 0] * nrm[i, 0]) + (t[i, 1] * nrm[i, 1])) + (t[i, 2] * nrm[i, 2]);
            for (int c = 0; c < 3; c++)
            {
                t[i, c] -= nrm[i, c] * dot;
            }
            double len = Math.Max(Math.Sqrt(((t[i, 0] * t[i, 0]) + (t[i, 1] * t[i, 1])) + (t[i, 2] * t[i, 2])), 1e-12);
            for (int c = 0; c < 3; c++)
            {
                t[i, c] /= len;
            }
            double cx = (nrm[i, 1] * t[i, 2]) - (nrm[i, 2] * t[i, 1]);
            double cy = (nrm[i, 2] * t[i, 0]) - (nrm[i, 0] * t[i, 2]);
            double cz = (nrm[i, 0] * t[i, 1]) - (nrm[i, 1] * t[i, 0]);
            double sign = (((cx * bt[i, 0]) + (cy * bt[i, 1])) + (cz * bt[i, 2])) < 0 ? -1.0 : 1.0;
            binormal[i, 0] = cx * sign;
            binormal[i, 1] = cy * sign;
            binormal[i, 2] = cz * sign;
        }
        return (t, binormal);
    }
    private static Dictionary<string, double[,]> Argb(Dictionary<string, double[,]> a)
    {
        Dictionary<string, double[,]> output = new(a);
        foreach (string name in new[] { "COLOR_0", "COLOR_1" })
        {
            if (a.TryGetValue(name, out double[,]? c) && c.GetLength(1) == 4)
            {
                double[,] argb = new double[c.GetLength(0), 4];
                for (int i = 0; i < c.GetLength(0); i++)
                {
                    argb[i, 0] = c[i, 3];
                    argb[i, 1] = c[i, 0];
                    argb[i, 2] = c[i, 1];
                    argb[i, 3] = c[i, 2];
                }
                output[name] = argb;
            }
        }
        return output;
    }
    public static byte[] EncodeVertices(string layout, bool big, Dictionary<string, double[,]> a)
    {
        (int stride, GdField[] fields) = GdLayouts.All[layout];
        int n = a["POSITION"].GetLength(0);
        byte[] output = new byte[n * stride];
        foreach (GdField f in fields)
        {
            a.TryGetValue(f.Name, out double[,]? v);
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < f.Count; c++)
                {
                    double x = v is not null && c < v.GetLength(1)
                        ? v[i, c]
                        : f.Name switch
                        {
                            "NORMAL" => c == 1 ? 1 : 0,
                            "COLOR_0" or "COLOR_1" => 255,
                            _ => 0,
                        };
                    if (v is not null && c >= v.GetLength(1))
                    {
                        throw new InvalidDataException($"{f.Name} has {v.GetLength(1)} components, the {layout} layout stores {f.Count}");
                    }
                    if (!double.IsNaN(f.Scale))
                    {
                        x = (x - f.Bias) / f.Scale;
                    }
                    int o = (i * stride) + f.Offset + (c * f.Size);
                    switch (f.Type)
                    {
                        case 'f':
                        {
                            float s = (float)x;
                            if (big)
                            {
                                BinaryPrimitives.WriteSingleBigEndian(output.AsSpan(o), s);
                            }
                            else
                            {
                                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(o), s);
                            }
                            break;
                        }
                        case 'B':
                            output[o] = (byte)Clip(x, 0, 255);
                            break;
                        case 'b':
                            output[o] = (byte)(sbyte)Clip(x, -128, 127);
                            break;
                        default:
                        {
                            short s = (short)Clip(x, short.MinValue, short.MaxValue);
                            if (big)
                            {
                                BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(o), s);
                            }
                            else
                            {
                                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(o), s);
                            }
                            break;
                        }
                    }
                }
            }
        }
        return output;
    }
    private static long Clip(double x, long lo, long hi)
    {
        double r = Math.Round(x, MidpointRounding.ToEven);
        if (double.IsNaN(r))
        {
            return lo == 0 ? 0 : 0;
        }
        return (long)Math.Clamp(r, lo, hi);
    }
    private static double[,] U8Weights(double[,] w)
    {
        int n = w.GetLength(0);
        double[,] o = new double[n, 4];
        for (int i = 0; i < n; i++)
        {
            double sum = ((w[i, 0] + w[i, 1]) + w[i, 2]) + w[i, 3];
            double div = Math.Max(sum, 1e-12);
            for (int k = 0; k < 4; k++)
            {
                o[i, k] = Math.Round(w[i, k] / div * 255, MidpointRounding.ToEven);
            }
        }
        return o;
    }
    private static double[,] Rows(double[,] v, List<int> rows)
    {
        int w = v.GetLength(1);
        double[,] o = new double[rows.Count, w];
        for (int i = 0; i < rows.Count; i++)
        {
            for (int c = 0; c < w; c++)
            {
                o[i, c] = v[rows[i], c];
            }
        }
        return o;
    }
    private static double[,] Tile(double[] row, int n)
    {
        double[,] o = new double[n, row.Length];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < row.Length; c++)
            {
                o[i, c] = row[c];
            }
        }
        return o;
    }
    private static byte[] U16s(IEnumerable<int> values, bool big, bool strict)
    {
        ByteWriter o = new();
        foreach (int v in values)
        {
            if (strict && v is < 0 or > 0xFFFF)
            {
                throw new OverflowException($"Python integer {v} out of bounds for uint16");
            }
            o.U16((uint)(ushort)v, big);
        }
        return o.ToArray();
    }
    private sealed class BinData(byte[] bin)
    {
        private readonly ByteWriter _new = Init(bin);
        private static ByteWriter Init(byte[] bin)
        {
            ByteWriter w = new(bin.Length + 1024);
            w.Add(bin);
            return w;
        }
        public int Length => _new.Length;
        public List<object?> Add(ReadOnlySpan<byte> data)
        {
            _new.Add(data);
            return [(long)(_new.Length - data.Length), (long)data.Length];
        }
        public byte[] ToArray()
        {
            return _new.ToArray();
        }
    }
    private static List<string> MaterialNames(JObj doc)
    {
        return [.. J.A(doc["materials"]).Select(m => J.S(J.O(m)["name"]))];
    }
    private static string NotOneOf(string material, List<string> names)
    {
        return $"glTF material '{material}' is not one of [{string.Join(", ", names.Select(n => $"'{n}'"))}]";
    }
    public static (JObj Doc, byte[] Bin) Import(JObj doc, byte[] bin, List<GltfPrim> prims)
    {
        Platform plat = Platforms.Parse(J.S(doc["platform"]));
        string kind = J.S(doc["kind"]);
        bool big = plat != Platform.N3ds;
        if (prims.Count == 0)
        {
            throw new InvalidDataException("the glTF has no triangles");
        }
        if (kind == "gds")
        {
            return ImportGds(doc, bin, prims);
        }
        if (kind == "gdw")
        {
            return ImportGdw(doc, bin, prims);
        }
        List<string> names = MaterialNames(doc);
        BinData nw = new(bin);
        Dictionary<string, List<double[,]>> attrs = [];
        List<string> order = [];
        List<int[,]> tris = [];
        List<int> mats = [];
        int baseIndex = 0;
        foreach (GltfPrim p in prims)
        {
            string name = Strip(p.Material);
            if (kind == "gdt" && !names.Contains(name))
            {
                throw new InvalidDataException(NotOneOf(p.Material, names));
            }
            Dictionary<string, double[,]> a = p.Attributes;
            int n = a["POSITION"].GetLength(0);
            _ = a.TryAdd("TEXCOORD_0", new double[n, 2]);
            _ = a.TryAdd("NORMAL", Tile([0.0, 1.0, 0.0], n));
            (a["TANGENT"], a["BINORMAL"]) = Tangents(a["POSITION"], a["NORMAL"], a["TEXCOORD_0"], p.Triangles);
            foreach ((string k, double[,] v) in a)
            {
                if (!attrs.TryGetValue(k, out List<double[,]>? list))
                {
                    attrs[k] = list = [];
                    order.Add(k);
                }
                list.Add(v);
            }
            int[,] shifted = (int[,])p.Triangles.Clone();
            for (int i = 0; i < shifted.GetLength(0); i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    shifted[i, c] += baseIndex;
                }
            }
            tris.Add(shifted);
            mats.Add(kind == "gdt" ? names.IndexOf(name) : 0);
            baseIndex += n;
        }
        int total = baseIndex;
        if (total > 0x10000)
        {
            throw new InvalidDataException($"{total} vertices; 16-bit indices allow 65536");
        }
        Dictionary<string, double[,]> all = [];
        foreach (string k in order)
        {
            if (attrs[k].Count == prims.Count)
            {
                all[k] = Concat(attrs[k]);
            }
        }
        double[,] pos = all["POSITION"];
        double[] sphere = Sphere(pos, null);
        JObj mesh = J.O(doc["mesh"]);
        if (kind == "gdi")
        {
            mesh["vertices"] = nw.Add(EncodeVertices(plat == Platform.Wii ? "wiigdi" : "rigid64", big, plat == Platform.Xbox360 ? Argb(all) : all));
            mesh["indices"] = nw.Add(U16s(TrisToStrip(ConcatTris(tris)), big, true));
            mesh["sphere"] = J.Doubles(sphere);
            return (doc, nw.ToArray());
        }
        JObj lod = J.O(J.A(mesh["lods"])[0]);
        List<object?> old = lod["draws"] as List<object?> ?? [];
        List<int> lst = [];
        List<object?> lodLists = J.A(lod["lists"]);
        for (int k = 0; k < lodLists.Count; k++)
        {
            for (int c = 0; c < J.I(lodLists[k]); c++)
            {
                lst.Add(k);
            }
        }
        Dictionary<long, (int List, JObj Draw)> keep = [];
        for (int j = old.Count - 1; j >= 0; j--)
        {
            JObj d = J.O(old[j]);
            keep[GdFile.GdtMaterial(plat, d)] = (lst[j], d);
        }
        string layout = plat switch
        {
            Platform.Wii => "wii24",
            Platform.N3ds => "ctr24",
            _ => "static72",
        };
        List<(int List, JObj Draw)> draws = [];
        for (int j = 0; j < mats.Count; j++)
        {
            int m = mats[j];
            int[,] t = tris[j];
            (int lk, JObj like) = keep.TryGetValue(m, out (int, JObj) hit)
                ? hit
                : (0, old.Count > 0 ? J.O(old[0]) : new JObj { ["flags"] = 0L, ["primitive"] = plat != Platform.N3ds ? 0x30000L : 0L });
            JObj d;
            if (plat == Platform.Wii)
            {
                List<int> s = TrisToStrip(t);
                d = new JObj
                {
                    ["material"] = 0L,
                    ["flags"] = (J.L(like["flags"]) & ~0xFF00L) | ((long)m << 8),
                    ["display_list"] = nw.Add(WiiList(0x9B, s)),
                };
            }
            else if (plat == Platform.N3ds)
            {
                d = new JObj
                {
                    ["material"] = (long)m,
                    ["primitive"] = like.Get("primitive", 0L),
                    ["flags"] = like["flags"],
                    ["indices"] = nw.Add(U16s(Flat(t), false, false)),
                };
            }
            else
            {
                d = new JObj
                {
                    ["material"] = 0L,
                    ["primitive"] = like.Get("primitive", 0x30000L),
                    ["flags"] = (J.L(like["flags"]) & 0xFFFFFF) | ((long)m << 24),
                    ["indices"] = nw.Add(U16s(TrisToStrip(t), true, true)),
                };
            }
            draws.Add((lk, d));
        }
        List<(int List, JObj Draw)> sorted = [.. draws.Select((x, i) => (x, i)).OrderBy(x => x.x.List).ThenBy(x => x.i).Select(x => x.x)];
        lod["draws"] = J.List(sorted.Select(x => (object?)x.Draw));
        lod["lists"] = J.Longs(Enumerable.Range(0, 4).Select(g => (long)sorted.Count(x => x.List == g)));
        lod["vertices"] = nw.Add(EncodeVertices(layout, big, plat == Platform.Xbox360 ? Argb(all) : all));
        lod.Remove("data_order");
        byte[] packed = new byte[16];
        for (int k = 0; k < 4; k++)
        {
            Bin.PutF32(packed, k * 4, sphere[k], big);
        }
        mesh["tail"] = J.Longs(Enumerable.Range(0, 4).Select(k => (long)Bin.U32(packed, k * 4, big)));
        return (doc, nw.ToArray());
    }
    private static byte[] WiiList(byte op, List<int> s)
    {
        ByteWriter dl = new();
        dl.Add(op);
        if (s.Count > 0xFFFF)
        {
            throw new StructException($"a display list of {s.Count} vertices");
        }
        dl.U16((uint)s.Count, true);
        foreach (int v in s)
        {
            if (v is < 0 or > 0xFFFF)
            {
                throw new OverflowException($"Python integer {v} out of bounds for uint16");
            }
            for (int k = 0; k < 4; k++)
            {
                dl.U16((uint)v, true);
            }
        }
        dl.Zeros(Bin.Mod(-dl.Length, 32));
        return dl.ToArray();
    }
    private static IEnumerable<int> Flat(int[,] t)
    {
        for (int i = 0; i < t.GetLength(0); i++)
        {
            for (int c = 0; c < t.GetLength(1); c++)
            {
                yield return t[i, c];
            }
        }
    }
    private static int[,] ConcatTris(List<int[,]> tris)
    {
        int total = tris.Sum(t => t.GetLength(0));
        int[,] o = new int[total, 3];
        int at = 0;
        foreach (int[,] t in tris)
        {
            for (int i = 0; i < t.GetLength(0); i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    o[at, c] = t[i, c];
                }
                at++;
            }
        }
        return o;
    }
    private static double[,] Concat(List<double[,]> parts)
    {
        int width = parts[0].GetLength(1);
        if (parts.Any(p => p.GetLength(1) != width))
        {
            throw new InvalidDataException("attribute arrays of different widths cannot be joined");
        }
        int total = parts.Sum(p => p.GetLength(0));
        double[,] o = new double[total, width];
        int at = 0;
        foreach (double[,] p in parts)
        {
            for (int i = 0; i < p.GetLength(0); i++)
            {
                for (int c = 0; c < width; c++)
                {
                    o[at, c] = p[i, c];
                }
                at++;
            }
        }
        return o;
    }
    private static double[] Sphere(double[,] pos, IReadOnlyList<int>? rows)
    {
        double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        int n = rows?.Count ?? pos.GetLength(0);
        if (n == 0)
        {
            throw new InvalidDataException("zero-size array to reduction operation minimum which has no identity");
        }
        for (int k = 0; k < n; k++)
        {
            int i = rows?[k] ?? k;
            for (int c = 0; c < 3; c++)
            {
                double v = pos[i, c];
                lo[c] = double.IsNaN(lo[c]) || double.IsNaN(v) ? double.NaN : Math.Min(lo[c], v);
                hi[c] = double.IsNaN(hi[c]) || double.IsNaN(v) ? double.NaN : Math.Max(hi[c], v);
            }
        }
        double dx = hi[0] - lo[0];
        double dy = hi[1] - lo[1];
        double dz = hi[2] - lo[2];
        return [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2, Math.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz)) / 2];
    }
    private static JObj? Like(List<object?> old, long material)
    {
        return old.Select(J.O).FirstOrDefault(d =>
            (d.Has("material") ? J.L(d["material"]) : J.L(d.Get("flags", 0L)) >> 24) == material);
    }
    private static int MaskOf(List<object?> old, long material, bool rigid)
    {
        JObj? like = Like(old, material);
        long mk = (like is null ? 0x07 : like.Has("mask") ? J.L(like["mask"]) : (J.L(like.Get("flags", 0L)) >> 8) & 0xFF) & 0x0F;
        return (int)(mk | (rigid ? 0x10L : 0));
    }
    private static long PartOf(List<object?> old, long material)
    {
        JObj? like = Like(old, material);
        return like is null ? 0 : like.Has("created") ? J.L(like["created"]) : (J.L(like.Get("flags", 0L)) >> 16) & 0xFF;
    }
    private static (JObj, byte[]) ImportGds(JObj doc, byte[] bin, List<GltfPrim> prims)
    {
        Platform plat = Platforms.Parse(J.S(doc["platform"]));
        bool big = plat != Platform.N3ds;
        List<string> names = MaterialNames(doc);
        Dictionary<string, List<int>> byName = [];
        List<object?> bones = J.A(J.O(J.A(doc["skeletons"])[0])["bones"]);
        for (int i = 0; i < bones.Count; i++)
        {
            string key = GdScene.BoneName(J.U(J.O(bones[i])["hash"]));
            if (!byName.TryGetValue(key, out List<int>? list))
            {
                byName[key] = list = [];
            }
            list.Add(i);
        }
        BinData nw = new(bin);
        JObj sm = J.O(J.A(J.O(doc["mesh"])["submeshes"])[0]);
        List<object?> old = J.A(sm["draws"]);
        List<(int Material, int? Bone, Dictionary<string, double[,]> A, int[,] Tri)> parts = [];
        foreach (GltfPrim p in prims)
        {
            string name = Strip(p.Material);
            if (!names.Contains(name))
            {
                throw new InvalidDataException(NotOneOf(p.Material, names));
            }
            Dictionary<string, double[,]> a = p.Attributes;
            int n = a["POSITION"].GetLength(0);
            _ = a.TryAdd("TEXCOORD_0", new double[n, 2]);
            _ = a.TryAdd("NORMAL", Tile([0.0, 1.0, 0.0], n));
            (a["TANGENT"], a["BINORMAL"]) = Tangents(a["POSITION"], a["NORMAL"], a["TEXCOORD_0"], p.Triangles);
            if (!a.TryGetValue("JOINTS_0", out double[,]? jsrc))
            {
                throw new InvalidDataException("a skinned model's primitives need joints and weights");
            }
            Dictionary<string, int> seen = [];
            int Bone(string jointName)
            {
                string key = Strip(jointName);
                int k = seen.GetValueOrDefault(key);
                seen[key] = k + 1;
                if (!byName.TryGetValue(key, out List<int>? hits))
                {
                    return -1;
                }
                return hits[Math.Min(k, hits.Count - 1)];
            }
            int[] lut = [.. p.Joints.Select(Bone)];
            int wj = jsrc.GetLength(1);
            if (wj != 4)
            {
                throw new InvalidDataException("JOINTS_0 holds 4 joints per vertex");
            }
            double[,] w = a["WEIGHTS_0"];
            double[,] j = new double[n, 4];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int ji = (int)jsrc[i, k];
                    if (ji < 0 || ji >= lut.Length)
                    {
                        throw new PyIndexException($"joint {ji} is past the skin's {lut.Length} joints");
                    }
                    if (lut[ji] < 0)
                    {
                        throw new InvalidDataException("a joint is not a bone of the model's skeleton");
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                int first = lut[(int)jsrc[i, 0]];
                for (int k = 0; k < 4; k++)
                {
                    j[i, k] = w[i, k] > 0 ? lut[(int)jsrc[i, k]] : first;
                }
            }
            a["JOINTS_0"] = j;
            int[] top = new int[n];
            bool allHeavy = true;
            for (int i = 0; i < n; i++)
            {
                double max = w[i, 0];
                int at = 0;
                for (int k = 1; k < 4 && !double.IsNaN(max); k++)
                {
                    if (double.IsNaN(w[i, k]) || w[i, k] > max)
                    {
                        max = w[i, k];
                        at = k;
                    }
                }
                top[i] = at;
                if (!(max > 0.999))
                {
                    allHeavy = false;
                }
            }
            bool rigid = allHeavy && Enumerable.Range(0, n).Select(i => j[i, top[i]]).Distinct().Count() == 1;
            int? rigidBone = rigid ? (int)j[0, top[0]] : null;
            parts.Add((names.IndexOf(name), rigidBone, a, p.Triangles));
        }
        List<object?> draws = [];
        if (plat == Platform.Wii)
        {
            List<byte[]> arrA = [];
            List<byte[]> arrB = [];
            List<byte[]> arrU = [];
            List<byte[]> arrC = [];
            int nA = 0;
            int nB = 0;
            int nU = 0;
            for (int k = 0; k < parts.Count; k++)
            {
                (int m, int? bone, Dictionary<string, double[,]> a, int[,] tri) = parts[k];
                int n = a["POSITION"].GetLength(0);
                int posBase;
                if (bone is null)
                {
                    double[,] w = a["WEIGHTS_0"];
                    double[,] wn = new double[n, 4];
                    for (int i = 0; i < n; i++)
                    {
                        double sum = ((w[i, 0] + w[i, 1]) + w[i, 2]) + w[i, 3];
                        for (int c = 0; c < 4; c++)
                        {
                            wn[i, c] = w[i, c] / sum;
                        }
                    }
                    Dictionary<string, double[,]> copy = new(a) { ["WEIGHTS_0"] = wn };
                    arrA.Add(EncodeVertices("wiiA", big, copy));
                    posBase = nA;
                    nA += n;
                }
                else
                {
                    arrB.Add(EncodeVertices("wiiB", big, a));
                    posBase = nB;
                    nB += n;
                }
                arrU.Add(EncodeVertices("wiiU", big, a));
                arrC.Add(EncodeVertices("wiiC", big, a));
                List<int> s = TrisToStrip(tri);
                ByteWriter dl = new();
                dl.Add(0x9C);
                dl.U16((uint)s.Count, true);
                foreach (int v in s)
                {
                    dl.U16((ushort)(v + posBase), true);
                    dl.U16((ushort)(v + posBase), true);
                    dl.U16((ushort)(v + nU), true);
                    dl.U16((ushort)(v + nU), true);
                }
                nU += n;
                dl.Zeros(Bin.Mod(-dl.Length, 32));
                draws.Add(new JObj
                {
                    ["material"] = (long)m,
                    ["created"] = PartOf(old, m),
                    ["mask"] = (long)MaskOf(old, m, bone is not null),
                    ["bone"] = (long)(bone ?? 0),
                    ["display_list"] = nw.Add(dl.Span),
                });
            }
            foreach ((string key, List<byte[]> data) in new[] { ("skinned", arrA), ("rigid", arrB), ("uv", arrU), ("colour", arrC) })
            {
                sm[key] = data.Count > 0 ? nw.Add([.. data.SelectMany(x => x)]) : null;
            }
            sm["draws"] = draws;
            sm.Remove("data_order");
            return (doc, nw.ToArray());
        }
        if (plat == Platform.PS3)
        {
            List<byte[]> sk = [];
            List<byte[]> rg = [];
            int ns = 0;
            int nr = 0;
            for (int k = 0; k < parts.Count; k++)
            {
                (int m, int? bone, Dictionary<string, double[,]> a, int[,] tri) = parts[k];
                int n = a["POSITION"].GetLength(0);
                int vbase;
                if (bone is null)
                {
                    Dictionary<string, double[,]> copy = new(a) { ["WEIGHTS_0"] = U8Weights(a["WEIGHTS_0"]) };
                    sk.Add(EncodeVertices("skin80", big, copy));
                    vbase = ns;
                    ns += n;
                }
                else
                {
                    rg.Add(EncodeVertices("rigid64", big, a));
                    vbase = nr;
                    nr += n;
                }
                long mk = MaskOf(old, m, bone is not null);
                int[,] shifted = (int[,])tri.Clone();
                for (int i = 0; i < shifted.GetLength(0); i++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        shifted[i, c] += vbase;
                    }
                }
                draws.Add(new JObj
                {
                    ["word"] = 0L,
                    ["flags"] = ((long)m << 24) | (PartOf(old, m) << 16) | (mk << 8) | (long)(bone ?? 0),
                    ["indices"] = nw.Add(U16s(TrisToStrip(shifted), true, true)),
                });
            }
            if (ns > 0x10000 || nr > 0x10000)
            {
                throw new InvalidDataException("more than 65536 vertices in one buffer");
            }
            List<object?> groups =
            [
                new JObj
                {
                    ["skinned"] = sk.Count > 0 ? nw.Add([.. sk.SelectMany(x => x)]) : null,
                    ["rigid"] = rg.Count > 0 ? nw.Add([.. rg.SelectMany(x => x)]) : null,
                    ["words"] = J.Longs(new long[] { 0, 0 }),
                },
            ];
            for (int g = 0; g < 3; g++)
            {
                groups.Add(new JObj { ["skinned"] = null, ["rigid"] = null, ["words"] = J.Longs(new long[] { 0, 0 }) });
            }
            sm["groups"] = groups;
        }
        else
        {
            int limit = plat == Platform.Xbox360 ? 48 : 24;
            foreach ((int m, int? bone, Dictionary<string, double[,]> a, int[,] tri) in parts)
            {
                List<(int[,] Tri, List<int> Pal)> pieces = bone is not null ? [(tri, [])] : Chunks(tri, a["JOINTS_0"], limit);
                foreach ((int[,] t, List<int> pal) in pieces)
                {
                    SortedSet<int> usedSet = [];
                    foreach (int v in Flat(t))
                    {
                        usedSet.Add(v);
                    }
                    List<int> used = [.. usedSet];
                    int[] remap = new int[a["POSITION"].GetLength(0)];
                    Array.Fill(remap, -1);
                    for (int i = 0; i < used.Count; i++)
                    {
                        remap[used[i]] = i;
                    }
                    Dictionary<string, double[,]> sub = [];
                    foreach ((string key, double[,] v) in a)
                    {
                        sub[key] = Rows(v, used);
                    }
                    int[,] t2 = new int[t.GetLength(0), 3];
                    for (int i = 0; i < t.GetLength(0); i++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            t2[i, c] = remap[t[i, c]];
                        }
                    }
                    JObj d = new()
                    {
                        ["words"] = J.Longs(new long[] { 0, 0 }),
                        ["palette"] = J.Longs(pal),
                        ["material"] = (long)m,
                        ["created"] = PartOf(old, m),
                        ["mask"] = (long)MaskOf(old, m, bone is not null),
                        ["bone"] = (long)(bone ?? 0),
                    };
                    if (pal.Count > 0)
                    {
                        double[,] js = sub["JOINTS_0"];
                        for (int i = 0; i < js.GetLength(0); i++)
                        {
                            for (int c = 0; c < js.GetLength(1); c++)
                            {
                                js[i, c] = SearchSorted(pal, js[i, c]);
                            }
                        }
                        sub["WEIGHTS_0"] = U8Weights(sub["WEIGHTS_0"]);
                    }
                    if (plat == Platform.Xbox360)
                    {
                        d["palette_rest"] = string.Concat(Enumerable.Repeat("00", 48 - pal.Count));
                        d["tail"] = "000000";
                        d["indices"] = nw.Add(U16s(TrisToStrip(t2), true, true));
                        d["vertices"] = nw.Add(EncodeVertices(pal.Count > 0 ? "skin72" : "rigid64", big, Argb(sub)));
                    }
                    else
                    {
                        d["palette_rest"] = string.Concat(Enumerable.Repeat("00", 24 - pal.Count));
                        d["b3b"] = 0L;
                        d["b3d"] = 0L;
                        d["b3f"] = 0L;
                        d["indices"] = nw.Add(U16s(Flat(t2), false, false));
                        d["skinned"] = pal.Count > 0 ? nw.Add(EncodeVertices("ctrskin", big, sub)) : [(long)nw.Length, 0L];
                        d["rigid"] = pal.Count > 0 ? [(long)nw.Length, 0L] : nw.Add(EncodeVertices("ctr24", big, sub));
                    }
                    draws.Add(d);
                }
            }
        }
        sm["draws"] = draws;
        sm["lists"] = J.Longs(new long[] { draws.Count, 0, 0, 0 });
        sm.Remove("data_order");
        return (doc, nw.ToArray());
    }
    private static int SearchSorted(List<int> pal, double v)
    {
        int lo = 0;
        int hi = pal.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pal[mid] < v)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
    private static List<(int[,], List<int>)> Chunks(int[,] tri, double[,] joints, int limit)
    {
        List<(int[,], List<int>)> output = [];
        List<int> cur = [];
        HashSet<int> used = [];
        for (int i = 0; i < tri.GetLength(0); i++)
        {
            HashSet<int> bones = [];
            for (int c = 0; c < 3; c++)
            {
                for (int k = 0; k < joints.GetLength(1); k++)
                {
                    bones.Add((int)joints[tri[i, c], k]);
                }
            }
            if (cur.Count > 0 && used.Union(bones).Count() > limit)
            {
                output.Add((Pick(tri, cur), [.. used.Order()]));
                cur = [];
                used = [];
            }
            cur.Add(i);
            used.UnionWith(bones);
        }
        if (cur.Count > 0)
        {
            output.Add((Pick(tri, cur), [.. used.Order()]));
        }
        return output;
    }
    private static int[,] Pick(int[,] tri, List<int> rows)
    {
        int[,] o = new int[rows.Count, 3];
        for (int i = 0; i < rows.Count; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                o[i, c] = tri[rows[i], c];
            }
        }
        return o;
    }
    private static (JObj, byte[]) ImportGdw(JObj doc, byte[] bin, List<GltfPrim> prims)
    {
        Platform plat = Platforms.Parse(J.S(doc["platform"]));
        bool big = plat != Platform.N3ds;
        List<string> names = MaterialNames(doc);
        JObj mesh = J.O(doc["mesh"]);
        Dictionary<long, long> flags = [];
        foreach (object? b in J.A(mesh["batches"]))
        {
            JObj batch = J.O(b);
            foreach (object? s in batch["subs"] as List<object?> ?? [])
            {
                foreach (object? d in J.O(s)["draws"] as List<object?> ?? [])
                {
                    flags.TryAdd(J.L(batch["material"]), J.L(J.O(d)["flags"]));
                }
            }
        }
        string layout = plat switch
        {
            Platform.Wii => "wii24",
            Platform.N3ds => "ctr24",
            _ => "static72",
        };
        BinData nw = new(bin);
        SortedDictionary<long, SortedDictionary<long, List<object?>>> groups = [];
        List<double[]> boxes = [];
        foreach (GltfPrim p in prims)
        {
            string name = Strip(p.Material);
            if (!names.Contains(name))
            {
                throw new InvalidDataException(NotOneOf(p.Material, names));
            }
            long m = names.IndexOf(name);
            Dictionary<string, double[,]> a = p.Attributes;
            int[,] t = p.Triangles;
            double[,] pos = a["POSITION"];
            int n = pos.GetLength(0);
            if (n > 0xFFFF)
            {
                throw new InvalidDataException($"a primitive has {n} vertices; a world draw holds 65535");
            }
            _ = a.TryAdd("TEXCOORD_0", new double[n, 2]);
            _ = a.TryAdd("NORMAL", Tile([0.0, 1.0, 0.0], n));
            (a["TANGENT"], a["BINORMAL"]) = Tangents(pos, a["NORMAL"], a["TEXCOORD_0"], t);
            SortedSet<int> used = [];
            foreach (int v in Flat(t))
            {
                used.Add(v);
            }
            double[,] scaled = new double[used.Count, 3];
            int row = 0;
            foreach (int v in used)
            {
                for (int c = 0; c < 3; c++)
                {
                    scaled[row, c] = pos[v, c] / 100;
                }
                row++;
            }
            double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
            if (used.Count == 0)
            {
                throw new InvalidDataException("zero-size array to reduction operation minimum which has no identity");
            }
            for (int i = 0; i < used.Count; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    lo[c] = Math.Min(lo[c], scaled[i, c]);
                    hi[c] = Math.Max(hi[c], scaled[i, c]);
                }
            }
            boxes.Add(lo);
            boxes.Add(hi);
            double dx = hi[0] - lo[0];
            double dy = hi[1] - lo[1];
            double dz = hi[2] - lo[2];
            JObj d = new()
            {
                ["sphere"] = J.Doubles(new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2, Math.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz)) / 2 }),
                ["leaf"] = 0L,
                ["e"] = 0xFFFFL,
                ["flags"] = flags.GetValueOrDefault(m, 3),
                ["vertices"] = nw.Add(EncodeVertices(layout, big, plat == Platform.Xbox360 ? Argb(a) : a)),
            };
            if (plat == Platform.Wii)
            {
                d["display_list"] = nw.Add(WiiList(0x9B, TrisToStrip(t)));
                d["words"] = J.Longs(new long[] { 0, 0 });
            }
            else
            {
                IEnumerable<int> idx = plat == Platform.N3ds ? Flat(t) : TrisToStrip(t);
                d["indices"] = nw.Add(U16s(idx, big, plat != Platform.N3ds));
                d["b"] = 0L;
                d["words"] = J.Longs(new long[] { 0, 0, 0, 0, 0, 0 });
            }
            if (!groups.TryGetValue(m, out SortedDictionary<long, List<object?>>? zones))
            {
                groups[m] = zones = [];
            }
            if (!zones.TryGetValue(p.Zone, out List<object?>? list))
            {
                zones[p.Zone] = list = [];
            }
            list.Add(d);
        }
        mesh["batches"] = J.List(groups.Select(g => (object?)new JObj
        {
            ["material"] = g.Key,
            ["subs"] = J.List(g.Value.Select(z => (object?)new JObj
            {
                ["zone"] = z.Key,
                ["words"] = J.Longs(new long[] { 0, 0 }),
                ["draws"] = z.Value,
            })),
        }));
        double[] min = [.. Enumerable.Range(0, 3).Select(c => boxes.Min(bx => bx[c]))];
        double[] max = [.. Enumerable.Range(0, 3).Select(c => boxes.Max(bx => bx[c]))];
        mesh["tree"] = new List<object?>
        {
            new JObj
            {
                ["min"] = J.Doubles(min),
                ["max"] = J.Doubles(max),
                ["children"] = new List<object?> { null, null, null, null },
            },
        };
        return (doc, nw.ToArray());
    }
}
