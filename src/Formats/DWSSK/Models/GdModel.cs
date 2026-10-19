using System.Buffers.Binary;
using DWSSK.Formats.Core;

namespace DWSSK.Formats.Models;

internal sealed class GdDraw
{
    public int Material { get; init; }
    public required int[] Indices { get; init; }
    public long Flags { get; init; }
    public bool Rigid { get; init; } = true;
    public int Bone { get; init; }
    public IReadOnlyList<int>? Palette { get; init; }
    public bool Strip { get; init; } = true;
    public int Zone { get; set; }
}

internal sealed class GdVertices(byte[] data, int count, string layout)
{
    public byte[] Data { get; } = data;
    public int Count { get; } = count;
    public string Layout { get; } = layout;
}

internal sealed class GdPrim(GdVertices vb)
{
    public GdVertices Vb { get; } = vb;
    public List<GdDraw> Draws { get; } = [];
}

internal sealed record GdSampler(int Texture, int WrapS, int WrapT, int Usage, int Filter, uint Param);

internal sealed record GdPass(int Shader, List<GdSampler> Samplers);

internal sealed record GdMaterial(string Name, uint Hash, List<GdPass> Passes);

internal sealed record GdBone(uint Hash, int? Parent, double[] InverseBind, double[] Rotation, double[] Scale, double[] Translation);

internal sealed class GdModel
{
    public required string Kind { get; init; }
    public Platform Platform { get; init; }
    public bool Big { get; init; }
    public required List<GdMaterial> Materials { get; init; }
    public required List<(uint Hash, float A, float B, uint C, uint D)> Textures { get; init; }
    public required List<uint[]> Shaders { get; init; }
    public required List<List<GdPrim>> Lods { get; init; }
    public required List<GdBone> Bones { get; init; }
}

internal sealed record GdField(string Name, int Offset, char Type, int Size, int Count, double Scale = double.NaN, double Bias = 0);

internal static class GdLayouts
{
    private static GdField F(string name, int off, int count)
    {
        return new GdField(name, off, 'f', 4, count);
    }

    private static GdField U1(string name, int off, int count, double scale = double.NaN, double bias = 0)
    {
        return new GdField(name, off, 'B', 1, count, scale, bias);
    }

    private static GdField I1(string name, int off, int count, double scale)
    {
        return new GdField(name, off, 'b', 1, count, scale);
    }

    private static GdField I2(string name, int off, int count, double scale)
    {
        return new GdField(name, off, 'h', 2, count, scale);
    }

    public static readonly Dictionary<string, (int Stride, GdField[] Fields)> All = new()
    {
        ["static72"] = (72, [F("POSITION", 0, 3), F("TEXCOORD_0", 12, 2), F("TEXCOORD_1", 20, 2), F("NORMAL", 28, 3),
            F("TANGENT", 40, 3), F("BINORMAL", 52, 3), U1("COLOR_0", 64, 4), U1("COLOR_1", 68, 4)]),
        ["rigid64"] = (64, [F("POSITION", 0, 3), F("TEXCOORD_0", 12, 2), F("NORMAL", 20, 3), F("TANGENT", 32, 3),
            F("BINORMAL", 44, 3), U1("COLOR_0", 56, 4), U1("COLOR_1", 60, 4)]),
        ["wii24"] = (24, [F("POSITION", 0, 3), I1("NORMAL", 12, 3, 1.0 / 64), U1("COLOR_0", 16, 4), I2("TEXCOORD_0", 20, 2, 1.0 / 1024)]),
        ["wiiskin"] = (40, [I2("POSITION", 0, 3, 1.0 / 16384), I1("NORMAL", 6, 3, 1.0 / 64), U1("JOINTS_0", 12, 4),
            F("WEIGHTS_0", 16, 4), I2("TEXCOORD_0", 32, 2, 1.0 / 1024), U1("COLOR_0", 36, 4)]),
        ["ctr24"] = (24, [F("POSITION", 0, 3), I2("TEXCOORD_0", 12, 2, 1.0 / 1024), U1("NORMAL", 16, 3, 1.0 / 127, -128.0 / 127),
            U1("COLOR_0", 20, 4)]),
        ["ctrskin"] = (32, [F("POSITION", 0, 3), I2("TEXCOORD_0", 12, 2, 1.0 / 1024), U1("NORMAL", 16, 3, 1.0 / 127, -128.0 / 127),
            U1("COLOR_0", 20, 4), U1("JOINTS_0", 24, 4), U1("WEIGHTS_0", 28, 4)]),
        ["wiiA"] = (32, [I2("POSITION", 0, 3, 1.0 / 16384), I1("NORMAL", 6, 3, 1.0 / 64), U1("JOINTS_0", 12, 4), F("WEIGHTS_0", 16, 4)]),
        ["wiiB"] = (10, [I2("POSITION", 0, 3, 1.0 / 16384), I1("NORMAL", 6, 3, 1.0 / 64)]),
        ["wiiU"] = (4, [I2("TEXCOORD_0", 0, 2, 1.0 / 1024)]),
        ["wiiC"] = (4, [U1("COLOR_0", 0, 4)]),
        ["wiigdi"] = (36, [F("POSITION", 0, 3), F("TEXCOORD_0", 12, 2), F("NORMAL", 20, 3), U1("COLOR_0", 32, 4)]),
        ["wiirigid"] = (18, [I2("POSITION", 0, 3, 1.0 / 16384), I1("NORMAL", 6, 3, 1.0 / 64), I2("TEXCOORD_0", 10, 2, 1.0 / 1024),
            U1("COLOR_0", 14, 4)]),
        ["skin72"] = (72, [F("POSITION", 0, 3), F("TEXCOORD_0", 12, 2), F("NORMAL", 20, 3), F("TANGENT", 32, 3),
            F("BINORMAL", 44, 3), U1("COLOR_0", 56, 4), U1("COLOR_1", 60, 4), U1("JOINTS_0", 64, 4), U1("WEIGHTS_0", 68, 4)]),
        ["skin80"] = (80, [F("POSITION", 0, 3), F("TEXCOORD_0", 12, 2), F("NORMAL", 20, 3), F("TANGENT", 32, 3),
            F("BINORMAL", 44, 3), U1("COLOR_0", 56, 4), U1("COLOR_1", 60, 4), U1("JOINTS_0", 64, 4), U1("WEIGHTS_0", 68, 4)]),
    };

    public static bool Has(string layout, string name)
    {
        return All[layout].Fields.Any(f => f.Name == name);
    }

    public static double[]? Attr(GdVertices vb, bool big, string name)
    {
        (int stride, GdField[] fields) = All[vb.Layout];
        GdField? field = fields.FirstOrDefault(f => f.Name == name);
        if (field is null)
        {
            return null;
        }
        double[] v = new double[vb.Count * field.Count];
        byte[] d = vb.Data;
        for (int i = 0; i < vb.Count; i++)
        {
            for (int c = 0; c < field.Count; c++)
            {
                int o = (i * stride) + field.Offset + (c * field.Size);
                double x = field.Type switch
                {
                    'f' => big ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)),
                    'B' => d[o],
                    'b' => (sbyte)d[o],
                    _ => big ? BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o)),
                };
                if (!double.IsNaN(field.Scale))
                {
                    x = (x * field.Scale) + field.Bias;
                }
                v[(i * field.Count) + c] = x;
            }
        }
        return v;
    }

    public static int Count(string layout, string name)
    {
        return All[layout].Fields.First(f => f.Name == name).Count;
    }
}

internal static class GdReader
{
    private static readonly string[] s_kinds = ["gdw", "gds", "gdi", "gdt"];

    public static int[] StripToTris(int[] s)
    {
        if (s.Length < 3)
        {
            return [];
        }
        List<int> output = new(s.Length * 3);
        for (int i = 0; i + 2 < s.Length; i++)
        {
            int a = s[i];
            int b = s[i + 1];
            int c = s[i + 2];
            if (a == b || b == c || a == c)
            {
                continue;
            }
            if (i % 2 == 1)
            {
                output.Add(b);
                output.Add(a);
                output.Add(c);
            }
            else
            {
                output.Add(a);
                output.Add(b);
                output.Add(c);
            }
        }
        return [.. output];
    }

    public static GdModel Parse(byte[] b, Platform platform)
    {
        return new Reader(b, platform).Parse();
    }

    private sealed class Reader(byte[] b, Platform platform)
    {
        private readonly bool _big = !(b.Length > 0 && b[0] != 0);

        private uint U(int o)
        {
            return Bin.U32(b, o, _big);
        }

        private uint[] Us(int o, int n)
        {
            return Bin.U32s(b, o, n, _big);
        }

        private ushort H(int o)
        {
            return Bin.U16(b, o, _big);
        }

        public GdModel Parse()
        {
            uint[] h = Us(0, 11);
            uint kindIndex = h[1] & 0xFF;
            string kind = kindIndex < s_kinds.Length ? s_kinds[kindIndex] : throw new InvalidDataException($"GD kind {kindIndex}");
            int nm = (int)h[2];
            int om = (int)h[3];
            int nt = (int)h[4];
            int ot = (int)h[5];
            int ns = (int)h[6];
            int os = (int)h[7];
            int mesh = (int)h[9];
            int skel = (int)h[10];
            List<(uint, float, float, uint, uint)> textures = [];
            for (int i = 0; i < nt; i++)
            {
                int q = ot + (i * 0x14);
                textures.Add((U(q), Bin.F32(b, q + 4, _big), Bin.F32(b, q + 8, _big), U(q + 12), U(q + 16)));
            }
            List<uint[]> shaders = [];
            for (int i = 0; i < ns; i++)
            {
                shaders.Add(Us(os + (i * 0x10), 4));
            }
            int msize = GdFile.MaterialSize(platform);
            List<GdMaterial> materials = [];
            for (int i = 0; i < nm; i++)
            {
                materials.Add(Material(om + (i * msize)));
            }
            List<List<GdPrim>> lods = kind switch
            {
                "gdt" => MeshGdt(mesh),
                "gdw" => MeshGdw(mesh),
                "gdi" => MeshGdi(mesh),
                _ => MeshGds(mesh),
            };
            return new GdModel
            {
                Kind = kind,
                Platform = platform,
                Big = _big,
                Materials = materials,
                Textures = textures,
                Shaders = shaders,
                Lods = lods,
                Bones = skel != 0 ? Skeleton(skel) : [],
            };
        }

        private GdMaterial Material(int o)
        {
            ReadOnlySpan<byte> field = Bin.Slice(b, o, o + 0x60);
            int nul = field.IndexOf((byte)0);
            string name = Bin.Latin1(nul < 0 ? field : field[..nul]);
            uint[] w = Us(o + 0x60, 24);
            List<GdPass> passes = [];
            int count = platform is Platform.Wii or Platform.N3ds ? 1 : 2;
            for (int p = 0; p < count; p++)
            {
                int start = (int)w[4 + (p * 5)];
                int end = (int)w[6 + (p * 5)];
                uint sh = w[7 + (p * 5)];
                if (platform == Platform.N3ds && start != 0)
                {
                    end = start;
                    while (end + 0x38 <= b.Length && !Bin.AnyNonZero(b.AsSpan(end, 8)))
                    {
                        end += 0x38;
                    }
                }
                List<GdSampler> samplers = [];
                int stop = start != 0 ? end - 0x37 : start;
                for (int r = start; r < stop; r += 0x38)
                {
                    ReadOnlySpan<byte> rec = Bin.Slice(b, r, r + 0x38);
                    if (rec.Length < 0x38)
                    {
                        Bin.Need(b, r, 0x38);
                    }
                    if (Bin.AnyNonZero(rec[..8]) || Bin.AnyNonZero(rec[0x0E..0x10]) || rec[0x0C] > 15 || rec[0x34] == 0)
                    {
                        continue;
                    }
                    samplers.Add(new GdSampler(
                        Bin.U16(rec, 8, _big), rec[10], rec[11], rec[12], rec[13], Bin.U32(rec, 0x10, _big)));
                }
                passes.Add(new GdPass((int)(sh & 0xFFFF), samplers));
            }
            return new GdMaterial(name, w[0], passes);
        }

        private int[] Strip(int cnt, int off)
        {
            Bin.Need(b, off, cnt * 2);
            int[] s = new int[cnt];
            for (int i = 0; i < cnt; i++)
            {
                s[i] = H(off + (i * 2));
            }
            return s;
        }

        private GdVertices Buffer(int count, int stride, int offset, string layout)
        {
            Bin.Need(b, offset, count * stride);
            return new GdVertices(b.AsSpan(offset, count * stride).ToArray(), count, layout);
        }

        private List<(int Op, int[][] Idx)> GxDl(int dl, int size, int perVertex)
        {
            List<(int, int[][])> output = [];
            int i = dl;
            int end = dl + size;
            while (i < end)
            {
                int op = Bin.U8(b, i);
                if (op == 0)
                {
                    i++;
                    continue;
                }
                if ((op & 0xF8) is not (0x80 or 0x90 or 0x98 or 0xA0))
                {
                    throw new InvalidDataException($"display list opcode 0x{op:x} at 0x{i:x}");
                }
                int cnt = Bin.U16(b, i + 1, true);
                Bin.Need(b, i + 3, cnt * perVertex * 2);
                int[][] idx = new int[cnt][];
                for (int k = 0; k < cnt; k++)
                {
                    idx[k] = new int[perVertex];
                    for (int c = 0; c < perVertex; c++)
                    {
                        idx[k][c] = Bin.U16(b, i + 3 + (((k * perVertex) + c) * 2), true);
                    }
                }
                output.Add((op & 0xF8, idx));
                i += 3 + (cnt * perVertex * 2);
            }
            return output;
        }

        private static (List<int[]> Unique, int[] Inverse) Unique(List<int[]> tuples)
        {
            int[] order = [.. Enumerable.Range(0, tuples.Count)];
            Array.Sort(order, (x, y) =>
            {
                int[] a = tuples[x];
                int[] c = tuples[y];
                for (int k = 0; k < a.Length; k++)
                {
                    int d = a[k].CompareTo(c[k]);
                    if (d != 0)
                    {
                        return d;
                    }
                }
                return x.CompareTo(y);
            });
            List<int[]> unique = [];
            int[] inverse = new int[tuples.Count];
            for (int k = 0; k < order.Length; k++)
            {
                int[] t = tuples[order[k]];
                if (unique.Count == 0 || !unique[^1].AsSpan().SequenceEqual(t))
                {
                    unique.Add(t);
                }
                inverse[order[k]] = unique.Count - 1;
            }
            return (unique, inverse);
        }

        private static (GdVertices Vb, List<GdDraw> Draws) GxDraw(byte[] vbuf, int stride, int vcount, string layout, List<(int Op, int[][] Idx)> prims, int material)
        {
            List<int[]> tuples = [.. prims.SelectMany(p => p.Idx)];
            (List<int[]> uniq, int[] inv) = Unique(tuples);
            if (!uniq.All(t => t.All(c => c == t[0])))
            {
                throw new ValueException("per-attribute indices differ");
            }
            byte[] data = new byte[uniq.Count * stride];
            for (int k = 0; k < uniq.Count; k++)
            {
                int src = uniq[k][0];
                if (src >= vcount)
                {
                    throw new InvalidDataException($"display list index {src} past the {vcount} vertices");
                }
                System.Buffer.BlockCopy(vbuf, src * stride, data, k * stride, stride);
            }
            List<int[]> strips = [];
            int at = 0;
            foreach ((int op, int[][] idx) in prims)
            {
                int n = idx.Length;
                int[] seq = inv[at..(at + n)];
                at += n;
                if (op == 0x98)
                {
                    strips.Add(seq);
                }
                else if (op == 0x90)
                {
                    if (seq.Length % 3 != 0)
                    {
                        throw new InvalidDataException($"a triangle list of {seq.Length} indices");
                    }
                    for (int t = 0; t + 2 < seq.Length; t += 3)
                    {
                        strips.Add([seq[t], seq[t + 1], seq[t + 2]]);
                    }
                }
                else if (op == 0xA0)
                {
                    for (int j = 1; j < n - 1; j++)
                    {
                        strips.Add([seq[0], seq[j], seq[j + 1]]);
                    }
                }
            }
            return (new GdVertices(data, uniq.Count, layout), [.. strips.Select(s => new GdDraw { Material = material, Indices = s })]);
        }

        private List<List<GdPrim>> MeshGdt(int mb)
        {
            uint fmt = U(mb);
            int nl = (int)U(mb + 4);
            if (fmt == 11)
            {
                return MeshGdtWii(mb, nl);
            }
            if (fmt == 10)
            {
                return MeshGdt3ds(mb, nl);
            }
            List<List<GdPrim>> lods = [];
            for (int l = 0; l < nl; l++)
            {
                int o = mb + 8 + (l * 0x30);
                uint[] p = Us(o + 8, 5);
                int vc = (int)U(o + 28);
                int vo = (int)U(o + 32);
                GdPrim prim = new(Buffer(vc, 72, vo, "static72"));
                for (int q = (int)p[0]; q < (int)p[4]; q += 0x14)
                {
                    uint[] v = Us(q, 5);
                    prim.Draws.Add(new GdDraw { Material = (int)(v[4] >> 24), Indices = Strip((int)v[1], (int)v[2]), Flags = v[4] });
                }
                lods.Add([prim]);
            }
            return lods;
        }

        private List<List<GdPrim>> MeshGdtWii(int mb, int nl)
        {
            List<List<GdPrim>> lods = [];
            for (int l = 0; l < nl; l++)
            {
                int o = mb + 8 + (l * 0x30);
                uint[] p = Us(o + 8, 5);
                int vc = (int)U(o + 36);
                int vo = (int)U(o + 40);
                Bin.Need(b, vo, vc * 24);
                byte[] vbuf = b.AsSpan(vo, vc * 24).ToArray();
                List<GdPrim> prims = [];
                for (int q = (int)p[0]; q < (int)p[4]; q += 0x10)
                {
                    uint[] v = Us(q, 4);
                    (GdVertices vb, List<GdDraw> draws) = GxDraw(vbuf, 24, vc, "wii24", GxDl((int)v[1], (int)v[2], 4), (int)((v[3] >> 8) & 0xFF));
                    GdPrim prim = new(vb);
                    prim.Draws.AddRange(draws);
                    prims.Add(prim);
                }
                lods.Add(prims);
            }
            return lods;
        }

        private List<List<GdPrim>> MeshGdt3ds(int mb, int nl)
        {
            List<List<GdPrim>> lods = [];
            for (int l = 0; l < nl; l++)
            {
                int o = mb + 8 + (l * 0x30);
                uint[] p = Us(o + 8, 5);
                int vc = (int)U(o + 36);
                int vo = (int)U(o + 40);
                GdPrim prim = new(Buffer(vc, 24, vo, "ctr24"));
                for (int q = (int)p[0]; q < (int)p[4]; q += 0x14)
                {
                    uint[] v = Us(q, 5);
                    prim.Draws.Add(new GdDraw { Material = (int)(v[0] & 0xFF), Indices = Strip((int)v[1], (int)v[2]), Flags = v[4], Strip = false });
                }
                lods.Add([prim]);
            }
            return lods;
        }

        private List<List<GdPrim>> MeshGdw(int mb)
        {
            int nb = (int)U(mb + 16);
            int bt = (int)U(mb + 20);
            List<GdPrim> prims = [];
            for (int i = 0; i < nb; i++)
            {
                int mat = H(bt + (i * 8));
                int nsub = H(bt + (i * 8) + 2);
                int p = (int)U(bt + (i * 8) + 4);
                for (int j = 0; j < nsub; j++)
                {
                    int zone = H(p + (j * 16));
                    int nd = H(p + (j * 16) + 2);
                    int dp = (int)U(p + (j * 16) + 4);
                    for (int k = 0; k < nd; k++)
                    {
                        if (platform == Platform.Wii)
                        {
                            int q = dp + (k * 0x2C) + 16;
                            int dl = (int)Bin.U32(b, q, true);
                            int size = (int)Bin.U32(b, q + 4, true);
                            int vc = Bin.U16(b, q + 10, true);
                            int vo = (int)Bin.U32(b, q + 16, true);
                            Bin.Need(b, vo, vc * 24);
                            byte[] vbuf = b.AsSpan(vo, vc * 24).ToArray();
                            (GdVertices vb, List<GdDraw> draws) = GxDraw(vbuf, 24, vc, "wii24", GxDl(dl, size, 4), mat);
                            foreach (GdDraw d in draws)
                            {
                                d.Zone = zone;
                            }
                            GdPrim prim = new(vb);
                            prim.Draws.AddRange(draws);
                            prims.Add(prim);
                            continue;
                        }
                        int dq = dp + (k * 0x3C);
                        int ic = H(dq + 2);
                        int ip = (int)U(dq + 4);
                        int vcount = H(dq + 8);
                        int fl = H(dq + 12);
                        int vofs = (int)U(dq + 16);
                        GdPrim pr = platform == Platform.N3ds
                            ? new GdPrim(Buffer(vcount, 24, vofs, "ctr24"))
                            : new GdPrim(Buffer(vcount, 72, vofs, "static72"));
                        pr.Draws.Add(new GdDraw
                        {
                            Material = mat,
                            Indices = Strip(ic, ip),
                            Flags = fl,
                            Strip = platform != Platform.N3ds,
                            Zone = zone,
                        });
                        prims.Add(pr);
                    }
                }
            }
            return [prims];
        }

        private List<List<GdPrim>> MeshGdi(int mb)
        {
            int vc = (int)U(mb + 8);
            int ic = (int)U(mb + 12);
            int vo = (int)U(mb + 0x24);
            int io = (int)U(mb + 0x28);
            (int stride, string layout) = platform == Platform.Wii ? (36, "wiigdi") : (64, "rigid64");
            GdPrim prim = new(Buffer(vc, stride, vo, layout));
            prim.Draws.Add(new GdDraw { Material = 0, Indices = Strip(ic, io) });
            return [[prim]];
        }

        private List<List<GdPrim>> MeshGds(int mb)
        {
            uint fmt = U(mb);
            int ns = (int)U(mb + 8);
            if (fmt == 6 && platform == Platform.Wii)
            {
                return MeshGdsWii(mb, ns);
            }
            if (fmt == 9)
            {
                return MeshGds3ds(mb, ns);
            }
            if (fmt == 6)
            {
                return MeshGds6(mb, ns);
            }
            List<List<GdPrim>> lods = [];
            for (int s = 0; s < ns; s++)
            {
                List<GdPrim> prims = [];
                lods.Add(prims);
                int o = mb + 12 + (s * 0x74);
                uint[] lists = Us(o, 5);
                for (int g = 0; g < 4; g++)
                {
                    uint[] v = Us(o + 20 + (g * 24), 6);
                    GdPrim skinned = new(Buffer((int)v[0], 80, (int)v[2], "skin80"));
                    GdPrim rigid = new(Buffer((int)v[1], 64, (int)v[3], "rigid64"));
                    for (int q = (int)lists[g]; q < (int)lists[g + 1]; q += 0x10)
                    {
                        uint[] d = Us(q, 4);
                        long fl = d[3];
                        bool isRigid = (fl & 0x1000) != 0;
                        (isRigid ? rigid : skinned).Draws.Add(new GdDraw
                        {
                            Material = (int)(fl >> 24),
                            Indices = Strip((int)d[0], (int)d[1]),
                            Flags = fl,
                            Rigid = isRigid,
                            Bone = (int)(fl & 0xFF),
                        });
                    }
                    if (skinned.Draws.Count > 0)
                    {
                        prims.Add(skinned);
                    }
                    if (rigid.Draws.Count > 0)
                    {
                        prims.Add(rigid);
                    }
                }
            }
            return lods;
        }

        private List<List<GdPrim>> MeshGdsWii(int mb, int ns)
        {
            List<List<GdPrim>> lods = [];
            for (int s = 0; s < ns; s++)
            {
                List<GdPrim> prims = [];
                lods.Add(prims);
                uint[] v = Us(mb + 12 + (s * 0x28), 10);
                int nd = (int)v[0];
                int dp = (int)v[1];
                byte[] a = Arr((int)v[2], 32, (int)v[3]);
                byte[] bb = Arr((int)v[4], 10, (int)v[5]);
                byte[] u = Arr((int)v[6], 4, (int)v[7]);
                byte[] c = Arr((int)v[8], 4, (int)v[9]);
                for (int d = 0; d < nd; d++)
                {
                    int q = dp + (d * 12);
                    int dl = (int)Bin.U32(b, q, true);
                    int size = (int)Bin.U32(b, q + 4, true);
                    int mat = Bin.U8(b, q + 8);
                    int mask = Bin.U8(b, q + 10);
                    int bone = Bin.U8(b, q + 11);
                    bool rigid = (mask & 0x10) != 0;
                    List<(int Op, int[][] Idx)> gx = GxDl(dl, size, 4);
                    if (gx.Count == 0)
                    {
                        throw new InvalidDataException($"draw {d} of submesh {s} has an empty display list");
                    }
                    List<int[]> tuples = [.. gx.SelectMany(x => x.Idx)];
                    (List<int[]> uniq, int[] inv) = Unique(tuples);
                    int stride = rigid ? 18 : 40;
                    byte[] data = new byte[uniq.Count * stride];
                    for (int k = 0; k < uniq.Count; k++)
                    {
                        int[] t = uniq[k];
                        int o = k * stride;
                        // GX sends a vertex's indices in attribute order: POS, NRM, CLR0, TEX0.
                        if (rigid)
                        {
                            Copy(bb, 10, t[0], 0, 10, data, o);
                            Copy(u, 4, t[3], 0, 4, data, o + 10);
                            Copy(c, 4, t[2], 0, 4, data, o + 14);
                        }
                        else
                        {
                            Copy(a, 32, t[0], 0, 32, data, o);
                            Copy(u, 4, t[3], 0, 4, data, o + 32);
                            Copy(c, 4, t[2], 0, 4, data, o + 36);
                        }
                    }
                    GdPrim prim = new(new GdVertices(data, uniq.Count, rigid ? "wiirigid" : "wiiskin"));
                    int at = 0;
                    foreach ((int op, int[][] idx) in gx)
                    {
                        int[] seq = inv[at..(at + idx.Length)];
                        at += idx.Length;
                        if (op == 0x98)
                        {
                            prim.Draws.Add(new GdDraw { Material = mat, Indices = seq, Flags = mask, Rigid = rigid, Bone = bone });
                        }
                        else
                        {
                            if (seq.Length % 3 != 0)
                            {
                                throw new InvalidDataException($"a triangle list of {seq.Length} indices");
                            }
                            for (int t = 0; t + 2 < seq.Length; t += 3)
                            {
                                prim.Draws.Add(new GdDraw { Material = mat, Indices = [seq[t], seq[t + 1], seq[t + 2]], Flags = mask, Rigid = rigid, Bone = bone });
                            }
                        }
                    }
                    prims.Add(prim);
                }
            }
            return lods;
        }

        private byte[] Arr(int count, int size, int offset)
        {
            if (count == 0)
            {
                return [];
            }
            Bin.Need(b, offset, count * size);
            return b.AsSpan(offset, count * size).ToArray();
        }

        private static void Copy(byte[] src, int stride, int index, int from, int n, byte[] dst, int at)
        {
            int o = (index * stride) + from;
            if (o + n > src.Length)
            {
                throw new InvalidDataException($"vertex array index {index} past its {src.Length / stride} entries");
            }
            System.Buffer.BlockCopy(src, o, dst, at, n);
        }

        private List<List<GdPrim>> MeshGds3ds(int mb, int ns)
        {
            List<List<GdPrim>> lods = [];
            for (int s = 0; s < ns; s++)
            {
                List<GdPrim> prims = [];
                lods.Add(prims);
                uint[] lists = Us(mb + 12 + (s * 20), 5);
                for (int q = (int)lists[0]; q < (int)lists[4]; q += 0x40)
                {
                    uint[] v = Us(q, 7);
                    int palLen = Bin.U8(b, q + 0x20);
                    int[] pal = [.. Bin.Slice(b, q + 0x21, q + 0x21 + palLen).ToArray().Select(x => (int)x)];
                    int mat = b[q + 0x39];
                    int mask = b[q + 0x3C];
                    int bone = b[q + 0x3E];
                    bool rigid = (mask & 0x10) != 0;
                    int[] idx = Strip((int)v[0], (int)v[1]);
                    GdVertices vb;
                    if (rigid)
                    {
                        vb = Buffer((int)v[5], 24, (int)v[6], "ctr24");
                    }
                    else
                    {
                        int n = v[0] != 0 ? idx.Max() + 1 : 0;
                        vb = Buffer(n, 32, (int)v[4], "ctrskin");
                    }
                    GdPrim prim = new(vb);
                    prim.Draws.Add(new GdDraw
                    {
                        Material = mat,
                        Indices = idx,
                        Flags = mask,
                        Rigid = rigid,
                        Bone = bone,
                        Palette = rigid ? null : pal,
                        Strip = false,
                    });
                    prims.Add(prim);
                }
            }
            return lods;
        }

        private List<List<GdPrim>> MeshGds6(int mb, int ns)
        {
            List<List<GdPrim>> lods = [];
            for (int s = 0; s < ns; s++)
            {
                List<GdPrim> prims = [];
                lods.Add(prims);
                uint[] lists = Us(mb + 12 + (s * 20), 5);
                for (int q = (int)lists[0]; q < (int)lists[4]; q += 0x50)
                {
                    uint[] v = Us(q, 5);
                    int palLen = Bin.U8(b, q + 0x18);
                    int[] pal = [.. Bin.Slice(b, q + 0x19, q + 0x19 + palLen).ToArray().Select(x => (int)x)];
                    int mat = b[q + 0x49];
                    int mask = b[q + 0x4B];
                    int bone = b[q + 0x4C];
                    bool rigid = (mask & 0x10) != 0;
                    (int stride, string layout) = rigid ? (64, "rigid64") : (72, "skin72");
                    GdPrim prim = new(Buffer((int)v[3], stride, (int)v[4], layout));
                    prim.Draws.Add(new GdDraw
                    {
                        Material = mat,
                        Indices = Strip((int)v[0], (int)v[1]),
                        Flags = mask,
                        Rigid = rigid,
                        Bone = bone,
                        Palette = rigid ? null : pal,
                    });
                    prims.Add(prim);
                }
            }
            return lods;
        }

        private List<GdBone> Skeleton(int o)
        {
            uint[] head = Us(o, 4);
            if (head[0] != 0x44444444)
            {
                return [];
            }
            int n = (int)head[1];
            int bo = (int)head[2];
            int po = (int)head[3];
            List<GdBone> bones = [];
            for (int i = 0; i < n; i++)
            {
                int q = bo + (i * 0x50);
                double[] m = [.. Bin.F32s(b, q, 16, _big).Select(x => (double)x)];
                uint hash = U(q + 0x40);
                int parent = Bin.U8(b, q + 0x48);
                int pq = po + (i * 0x30);
                bones.Add(new GdBone(
                    hash,
                    parent == 0xFF ? null : parent,
                    m,
                    [.. Bin.F32s(b, pq, 4, _big).Select(x => (double)x)],
                    [.. Bin.F32s(b, pq + 0x10, 3, _big).Select(x => (double)x)],
                    [.. Bin.F32s(b, pq + 0x20, 3, _big).Select(x => (double)x)]));
            }
            return bones;
        }
    }
}

internal sealed class ValueException(string message) : Exception(message);
