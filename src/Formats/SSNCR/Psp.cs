using System.Buffers.Binary;
using System.Numerics;
using Omega.Plugins;
namespace SSNCR.Formats;

internal sealed class PspChunks
{
    public const ushort Header = 1, Relocations = 2, Strings = 3, GeRelocations = 16, TextureBinds = 17;
    public const ushort TexTable = 18, DisplayLists = 19, Vertices = 20, Clut = 21, Pixels = 22, Model = 23;
    private static readonly ushort[] ModelBlocks = [Model, DisplayLists, Vertices, Strings];
    private static readonly ushort[] TexBlocks = [TexTable, DisplayLists, Clut, Pixels];

    public byte[][] Blocks { get; } = new byte[4][];
    public Dictionary<ushort, byte[]> Tables { get; } = [];

    public static bool Is(ReadOnlySpan<byte> d)
        => d.Length >= 22 && BinaryPrimitives.ReadUInt32LittleEndian(d) == 6
            && BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) == Header
            && BinaryPrimitives.ReadUInt16LittleEndian(d[10..]) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(d[12..]) == 0;

    public static bool IsTexture(ReadOnlySpan<byte> d) => Is(d) && BinaryPrimitives.ReadUInt16LittleEndian(d[18..]) == TexTable;
    public static bool IsModel(ReadOnlySpan<byte> d) => Is(d) && BinaryPrimitives.ReadUInt16LittleEndian(d[18..]) == Model;

    public static PspChunks Read(ReadOnlySpan<byte> d)
    {
        if (!Is(d)) throw new InvalidDataException("not a Torus PSP chunk stream");
        var r = new PspChunks();
        bool tex = IsTexture(d);
        int p = 0;
        while (p < d.Length)
        {
            if (p + 8 > d.Length) throw new InvalidDataException("truncated PSP chunk header");
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[p..]);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 4)..]);
            if (size < 0 || p + 8 + size > d.Length) throw new InvalidDataException($"PSP chunk {type} overruns the file");
            byte[] payload = d.Slice(p + 8, size).ToArray();
            int slot = Array.IndexOf(tex ? TexBlocks : ModelBlocks, type);
            if (slot >= 0) r.Blocks[slot] = payload;
            else if (type != Header) r.Tables[type] = payload;
            p += 8 + size;
        }
        for (int i = 0; i < 4; i++) r.Blocks[i] ??= [];
        return r;
    }

    public uint[] Entries(ushort table)
    {
        if (!Tables.TryGetValue(table, out byte[]? t) || t.Length < 4) return [];
        int n = BinaryPrimitives.ReadInt32LittleEndian(t);
        if (n < 0 || 4 + 4L * n > t.Length) throw new InvalidDataException($"PSP table {table} is short");
        var e = new uint[n];
        for (int i = 0; i < n; i++) e[i] = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4 + 4 * i));
        return e;
    }

    public (byte[] Block, int Offset) Resolve(uint encoded)
    {
        int b = (int)(encoded >> 24), off = (int)(encoded & 0xFFFFFF);
        if (b > 3 || off > Blocks[b].Length) throw new InvalidDataException($"PSP pointer 0x{encoded:X8} is outside its block");
        return (Blocks[b], off);
    }

    public string String(int offset)
    {
        byte[] s = Blocks[3];
        int at = 4 + offset;
        if (at < 4 || at >= s.Length) throw new InvalidDataException("PSP string offset outside the string table");
        return Rw.CString(s.AsSpan(at));
    }
}

internal static class PspGe
{
    public static float Float24(uint cmd) => BitConverter.UInt32BitsToSingle(cmd << 8);

    public static (int Size, int Wt, int Tc, int Col, int Nrm, int Pos) Layout(uint vt)
    {
        int tc = (int)(vt & 3), col = (int)((vt >> 2) & 7), nrm = (int)((vt >> 5) & 3), pos = (int)((vt >> 7) & 3);
        int wt = (int)((vt >> 9) & 3), nw = (int)((vt >> 14) & 7) + 1;
        if (((vt >> 11) & 3) != 0 || ((vt >> 18) & 7) != 0 || ((vt >> 23) & 1) != 0)
            throw new NotSupportedException($"GE vertex type 0x{vt:X6}");
        int size = 0, align = 1;
        void Add(int bytes, int al) { size = (size + al - 1) / al * al; size += bytes; align = Math.Max(align, al); }
        int[] one = [0, 1, 2, 4];
        int wAt = size; if (wt != 0) { Add(one[wt] * nw, one[wt]); wAt = size - one[wt] * nw; }
        int tAt = size; if (tc != 0) { Add(one[tc] * 2, one[tc]); tAt = size - one[tc] * 2; }
        int cAt = size; if (col != 0) { int cb = col == 7 ? 4 : 2; Add(cb, cb); cAt = size - cb; }
        int nAt = size; if (nrm != 0) { Add(one[nrm] * 3, one[nrm]); nAt = size - one[nrm] * 3; }
        if (pos == 0) throw new NotSupportedException("GE vertex type without positions");
        Add(one[pos] * 3, one[pos]);
        int pAt = size - one[pos] * 3;
        size = (size + align - 1) / align * align;
        return (size, wAt, tAt, cAt, nAt, pAt);
    }
}

internal static partial class TrmFile
{
    public static TrmModel ReadPsp(byte[] data)
    {
        PspChunks c = PspChunks.Read(data);
        byte[] m = c.Blocks[0];
        if (m.Length < 0x48) throw new InvalidDataException("PSP model struct is short");
        int U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(o));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(o));
        int numAtomics = U16(4), numFrames = U16(6), numInst = U16(8), numHash = U16(0xC);
        int Field(int o) { uint v = U32(o); if (v == 0) return -1; (byte[] b, int off) = c.Resolve(v); if (b != m) throw new InvalidDataException("PSP model pointer leaves the model block"); return off; }
        int atomicsAt = Field(0x18), instAt = Field(0x1C), hashAt = Field(0x20), parentAt = Field(0x28), matAt = Field(0x2C);

        var inst = new TrmInstance[numInst];
        for (int i = 0; i < numInst; i++)
        {
            int p = instAt + 8 * i;
            int frame = U16(p), atomic = U16(p + 2);
            if (frame >= numFrames || atomic >= numAtomics) throw new InvalidDataException($"instance {i} points outside the model");
            inst[i] = new TrmInstance(frame, atomic, U32(p + 4));
        }
        var hashOf = new uint[numFrames];
        for (int i = 0; i < numHash; i++)
        {
            uint f = U32(hashAt + 8 * i + 4);
            if (f < numFrames) hashOf[f] = U32(hashAt + 8 * i);
        }
        var instanceNames = new Dictionary<int, uint>();
        int named = U16(0xE), namedAt = named > 0 ? Field(0x24) : -1;
        for (int i = 0; i < named; i++)
        {
            uint index = U32(namedAt + 8 * i + 4);
            if (index >= numInst) throw new InvalidDataException($"named instance {i} points outside the instances");
            instanceNames.TryAdd((int)index, U32(namedAt + 8 * i));
        }
        var frames = new TrmFrame[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            int parent = (short)U16(parentAt + 2 * i);
            if (parent >= i) throw new InvalidDataException($"frame {i} has parent {parent} after it");
            var f = new float[16];
            for (int k = 0; k < 16; k++) f[k] = BitConverter.Int32BitsToSingle((int)U32(matAt + 64 * i + 4 * k));
            frames[i] = new TrmFrame(parent, MatrixOps.ToMatrix(f), hashOf[i]);
        }

        Dictionary<int, string> textureAt = [];
        foreach (uint site in c.Entries(PspChunks.TextureBinds))
        {
            (byte[] b, int off) = c.Resolve(site);
            if (b != c.Blocks[1] || off + 8 > b.Length) throw new InvalidDataException("PSP texture bind outside the display lists");
            textureAt[off] = c.String((int)(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off)) & 0xFFFFFF));
        }

        var atomics = new List<TrmAtomic>(numAtomics);
        for (int a = 0; a < numAtomics; a++)
            atomics.Add(ReadPspAtomic(c, m, atomicsAt + 0x34 * a, textureAt));

        return new TrmModel
        {
            BigEndian = false, Name = "", Frames = frames, Instances = inst, Atomics = atomics,
            InstanceNames = instanceNames, FadeFrom = U16(0xA),
            NumAnimations = U16(0x10), AnimationBytes = 0,
            Animations = U16(0x10) > 0 ? TrmAnimReader.ReadPsp(c, m, Field(0x38), U16(0x10)) : [],
            NumTracks = 0,
        };
    }

    private static TrmAtomic ReadPspAtomic(PspChunks c, byte[] m, int rec, Dictionary<int, string> textureAt)
    {
        int subLists = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(rec + 4));
        uint target = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(rec + 0x28));
        List<(int Dl, uint Palette)> lists = [];
        byte[]? used = null;
        int invAt = -1;
        if (subLists == 0)
        {
            (byte[] b, int off) = c.Resolve(target);
            if (b != c.Blocks[1]) throw new InvalidDataException("PSP atomic display list is not in the display list block");
            lists.Add((off, 0));
        }
        else
        {
            (byte[] b, int s) = c.Resolve(target);
            if (b != m) throw new InvalidDataException("PSP skin record is not in the model block");
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(s));
            int dlList = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(s + 4))).Offset;
            int palList = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(s + 8))).Offset;
            int usedAt = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(s + 12))).Offset;
            used = m.AsSpan(usedAt, count).ToArray();
            invAt = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(s + 16))).Offset;
            if (invAt + 64L * count > m.Length) throw new InvalidDataException("PSP inverse bind matrices run off the model block");
            for (int i = 0; i < subLists; i++)
            {
                (byte[] db, int doff) = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(dlList + 4 * i)));
                if (db != c.Blocks[1]) throw new InvalidDataException("PSP skin display list is not in the display list block");
                lists.Add((doff, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(palList + 4 * i))));
            }
        }

        byte[] dlb = c.Blocks[1], vb = c.Blocks[2];
        List<float> pos = [], nrm = [], uv = [], wts = [];
        List<byte> col = [], jnt = [];
        bool anyN = false, anyC = false, anyT = false;
        List<(int Material, List<int> Tris)> groups = [];
        List<TrmMaterial> materials = [];
        Dictionary<string, int> matIndex = [];
        uint vtype = 0;
        long vaddr = -1, baseHi = 0;
        float su = 1, sv = 1, ou = 0, ov = 0;
        int material = -1;
        bool flip = false;
        foreach ((int start, uint palette) in lists)
        {
            for (int p = start; ; p += 4)
            {
                if (p + 4 > dlb.Length) throw new InvalidDataException("PSP display list runs off its block");
                if (textureAt.TryGetValue(p, out string? tex))
                {
                    if (!matIndex.TryGetValue(tex, out material)) { material = materials.Count; matIndex[tex] = material; materials.Add(new TrmMaterial(0xFFFFFFFF, tex)); }
                    p += 4;
                    continue;
                }
                uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(dlb.AsSpan(p));
                int op = (int)(cmd >> 24);
                if (op == 0x0B) break;
                switch (op)
                {
                    case 0x12: vtype = cmd & 0xFFFFFF; break;
                    case 0x10: baseHi = (cmd >> 16) & 0xF; break;
                    case 0x01: vaddr = (baseHi << 24) | (cmd & 0xFFFFFF); break;
                    case 0x9B: flip = (cmd & 1) != 0; break;
                    case 0x48: su = PspGe.Float24(cmd); break;
                    case 0x49: sv = PspGe.Float24(cmd); break;
                    case 0x4A: ou = PspGe.Float24(cmd); break;
                    case 0x4B: ov = PspGe.Float24(cmd); break;
                    case 0x08 or 0x09 or 0x0A:
                        throw new NotSupportedException($"GE flow command 0x{op:X2} in a model display list");
                    case 0x04:
                        {
                            int prim = (int)((cmd >> 16) & 7), n = (int)(cmd & 0xFFFF);
                            if (prim != 4) throw new NotSupportedException($"GE primitive {prim}");
                            if (vaddr < 0 || (vaddr >> 24) != 2) throw new InvalidDataException("GE draw before a vertex address");
                            if (material < 0)
                            {
                                if (!matIndex.TryGetValue("", out material)) { material = materials.Count; matIndex[""] = material; materials.Add(new TrmMaterial(0xFFFFFFFF, null)); }
                            }
                            var lay = PspGe.Layout(vtype);
                            int tc = (int)(vtype & 3), cf = (int)((vtype >> 2) & 7), nf = (int)((vtype >> 5) & 3), wf = (int)((vtype >> 9) & 3), nw = (int)((vtype >> 14) & 7) + 1;
                            if ((vtype >> 7 & 3) != 3 || (tc != 0 && tc != 2) || (nf != 0 && nf != 1) || (wf != 0 && wf != 2))
                                throw new NotSupportedException($"GE vertex type 0x{vtype:X6}");
                            if (subLists > 0 && wf == 0) throw new InvalidDataException("skinned PSP list without weights");
                            int off = (int)(vaddr & 0xFFFFFF);
                            if (off + (long)n * lay.Size > vb.Length) throw new InvalidDataException("GE draw reads past the vertex block");
                            int first = pos.Count / 3;
                            for (int v = 0; v < n; v++)
                            {
                                ReadOnlySpan<byte> x = vb.AsSpan(off + v * lay.Size, lay.Size);
                                for (int k = 0; k < 3; k++) pos.Add(BinaryPrimitives.ReadSingleLittleEndian(x[(lay.Pos + 4 * k)..]));
                                anyN |= nf != 0; anyC |= cf != 0; anyT |= tc != 0;
                                if (nf != 0) { nrm.Add((sbyte)x[lay.Nrm] / 127f); nrm.Add((sbyte)x[lay.Nrm + 1] / 127f); nrm.Add((sbyte)x[lay.Nrm + 2] / 127f); }
                                else { nrm.Add(0); nrm.Add(0); nrm.Add(0); }
                                if (tc != 0)
                                {
                                    uv.Add(BinaryPrimitives.ReadUInt16LittleEndian(x[lay.Tc..]) / 32768f * su + ou);
                                    uv.Add(BinaryPrimitives.ReadUInt16LittleEndian(x[(lay.Tc + 2)..]) / 32768f * sv + ov);
                                }
                                else { uv.Add(0); uv.Add(0); }
                                AddColor(col, cf, x[lay.Col..]);
                                if (subLists > 0)
                                {
                                    for (int k = 0; k < 4; k++)
                                    {
                                        int slot = (int)((palette >> (8 * k)) & 0xFF);
                                        bool live = k < nw;
                                        if (live && (slot == 0 || slot > used!.Length)) throw new InvalidDataException("PSP skin palette slot outside the used bones");
                                        jnt.Add(live ? used![slot - 1] : (byte)0);
                                        wts.Add(live ? BinaryPrimitives.ReadUInt16LittleEndian(x[(lay.Wt + 2 * k)..]) / 32768f : 0f);
                                    }
                                }
                            }
                            vaddr += (long)n * lay.Size;
                            List<int> tris = groups.FirstOrDefault(g => g.Material == material).Tris ?? Add(groups, material);
                            for (int v = 2; v < n; v++)
                            {
                                int a = first + v - 2, b = first + v - 1, e = first + v;
                                if (Same(pos, a, b) || Same(pos, b, e) || Same(pos, a, e)) continue;
                                if (((v & 1) == 0) != flip) { tris.Add(a); tris.Add(b); tris.Add(e); }
                                else { tris.Add(b); tris.Add(a); tris.Add(e); }
                            }
                            break;
                        }
                }
            }
        }

        List<byte> attrs = [9];
        if (anyN) attrs.Add(10);
        if (anyC) attrs.Add(11);
        if (anyT) attrs.Add(13);
        int per = attrs.Count;
        var strips = groups.Select(g => new TrmStrip(g.Material, [.. g.Tris.SelectMany(i => Enumerable.Repeat(i, per))], true)).ToList();
        TrmSkin? skin = null;
        if (subLists > 0)
        {
            int bones = used!.Length == 0 ? 0 : used.Max() + 1;
            var inv = Enumerable.Repeat(Matrix4x4.Identity, bones).ToArray();
            for (int k = 0; k < used.Length; k++)
            {
                var f = new float[16];
                for (int e = 0; e < 16; e++) f[e] = BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(invAt + 64 * k + 4 * e));
                f[3] = f[7] = f[11] = 0f; f[15] = 1f;
                inv[used[k]] = MatrixOps.ToMatrix(f);
            }
            skin = new TrmSkin(bones, used, 4, [.. jnt], [.. wts], inv);
        }
        return new TrmAtomic
        {
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(rec)), NumVerts = pos.Count / 3, Materials = materials, Attributes = [.. attrs],
            Positions = [.. pos], Normals = anyN ? [.. nrm] : null, Colors = anyC ? [.. col] : null,
            Uv0 = anyT ? [.. uv] : null, Strips = strips, Skin = skin,
        };

        static List<int> Add(List<(int, List<int>)> g, int mat) { List<int> l = []; g.Add((mat, l)); return l; }
    }

    private static void AddColor(List<byte> col, int fmt, ReadOnlySpan<byte> x)
    {
        switch (fmt)
        {
            case 0: col.Add(255); col.Add(255); col.Add(255); col.Add(255); break;
            case 4:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(x);
                    int r = v & 31, g = (v >> 5) & 63, b = (v >> 11) & 31;
                    col.Add((byte)(r << 3 | r >> 2)); col.Add((byte)(g << 2 | g >> 4)); col.Add((byte)(b << 3 | b >> 2)); col.Add(255);
                    break;
                }
            case 5:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(x);
                    int r = v & 31, g = (v >> 5) & 31, b = (v >> 10) & 31;
                    col.Add((byte)(r << 3 | r >> 2)); col.Add((byte)(g << 3 | g >> 2)); col.Add((byte)(b << 3 | b >> 2)); col.Add((byte)((v >> 15) * 255));
                    break;
                }
            case 6:
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(x);
                    for (int k = 0; k < 4; k++) col.Add((byte)(((v >> (4 * k)) & 15) * 17));
                    break;
                }
            case 7: for (int k = 0; k < 4; k++) col.Add(x[k]); break;
            default: throw new NotSupportedException($"GE color format {fmt}");
        }
    }
}

internal sealed record PspTexEntry(string Name, int Width, int Height, int Psm, bool Swizzled, int BufWidth,
    long Pixels, long Clut, int ClutFormat, int ClutShift, int ClutMask, int ClutStart, int ClutBlocks);

internal static class PspTexFile
{
    public static bool IsPspTex(ReadOnlySpan<byte> data) => PspChunks.IsTexture(data);

    public static IReadOnlyList<PspTexEntry> Read(byte[] data)
    {
        PspChunks c = PspChunks.Read(data);
        byte[] t = c.Blocks[0];
        if (t.Length < 12) throw new InvalidDataException("PSP texture table is short");
        int count = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(4));
        (byte[] tb, int table) = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(8)));
        if (tb != t || count < 0 || table + 0x34L * count > t.Length) throw new InvalidDataException("PSP texture table is outside its block");
        List<PspTexEntry> list = new(count);
        for (int i = 0; i < count; i++)
        {
            int e = table + 0x34 * i;
            string name = Rw.CString(t.AsSpan(e + 0x12, 32));
            (byte[] db, int dl) = c.Resolve(BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(e)));
            if (db != c.Blocks[1]) throw new InvalidDataException($"PSP texture '{name}' display list is misplaced");
            PspTexEntry te = FromDisplayList(name, db, dl);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e + 10)), h = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(e + 12));
            if (w > 0 && h > 0 && w <= te.Width && h <= te.Height) te = te with { Width = w, Height = h };
            list.Add(te);
        }
        return list;
    }

    private static PspTexEntry FromDisplayList(string name, byte[] dl, int p)
    {
        int psm = -1, w = 0, h = 0, bw = 0, cfmt = 0, cshift = 0, cmask = 0xFF, cstart = 0, cblocks = 0;
        long tbp = 0, tbwHi = 0, cbp = 0, cbwHi = 0;
        bool swz = false;
        for (; ; p += 4)
        {
            if (p + 4 > dl.Length) throw new InvalidDataException($"PSP texture '{name}' display list runs off its block");
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(dl.AsSpan(p));
            int op = (int)(cmd >> 24);
            if (op == 0x0B) break;
            switch (op)
            {
                case 0xC3: psm = (int)(cmd & 0xF); break;
                case 0xC2: swz = (cmd & 1) != 0; break;
                case 0xA0: tbp = cmd & 0xFFFFFF; break;
                case 0xA8: bw = (int)(cmd & 0xFFFF); tbwHi = (cmd >> 16) & 0xFF; break;
                case 0xB8: w = 1 << (int)(cmd & 0xF); h = 1 << (int)((cmd >> 8) & 0xF); break;
                case 0xB0: cbp = cmd & 0xFFFFFF; break;
                case 0xB1: cbwHi = (cmd >> 16) & 0xFF; break;
                case 0xC4: cblocks = (int)(cmd & 0x3F); break;
                case 0xC5: cfmt = (int)(cmd & 3); cshift = (int)((cmd >> 2) & 31); cmask = (int)((cmd >> 8) & 0xFF); cstart = (int)((cmd >> 16) & 31); break;
            }
        }
        if (psm < 0 || w == 0 || bw == 0) throw new InvalidDataException($"PSP texture '{name}' display list sets no texture");
        return new PspTexEntry(name, w, h, psm, swz, bw, (tbwHi << 24) | tbp, (cbwHi << 24) | cbp, cfmt, cshift, cmask, cstart, cblocks);
    }

    private static readonly int[] Bits = [16, 16, 16, 32, 4, 8, 16, 32];

    public static byte[] Decode(PspTexEntry e, byte[] data)
    {
        PspChunks c = PspChunks.Read(data);
        if (e.Psm > 7) throw new NotSupportedException($"PSP texture format {e.Psm}");
        int bits = Bits[e.Psm];
        int rowBytes = e.BufWidth * bits / 8;
        int need = rowBytes * e.Height;
        (byte[] pb, int po) = c.Resolve((uint)e.Pixels);
        if (po + need > pb.Length) throw new InvalidDataException($"PSP texture '{e.Name}' pixels run past their block");
        ReadOnlySpan<byte> src = pb.AsSpan(po, need);
        byte[] lin = e.Swizzled ? Unswizzle(src, rowBytes, e.Height) : src.ToArray();
        uint[]? clut = null;
        if (e.Psm >= 4)
        {
            (byte[] cb, int co) = c.Resolve((uint)e.Clut);
            int esize = e.ClutFormat == 3 ? 4 : 2;
            int cbytes = Math.Max(e.ClutBlocks * 32, (e.Psm == 4 ? 16 : 256) * esize);
            cbytes = Math.Min(cbytes, cb.Length - co);
            if (cbytes < e.ClutBlocks * 32) throw new InvalidDataException($"PSP texture '{e.Name}' CLUT runs past its block");
            clut = new uint[cbytes / esize];
            for (int i = 0; i < clut.Length; i++)
                clut[i] = Color(e.ClutFormat, esize == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(cb.AsSpan(co + 4 * i)) : BinaryPrimitives.ReadUInt16LittleEndian(cb.AsSpan(co + 2 * i)));
        }
        var rgba = new byte[e.Width * e.Height * 4];
        for (int y = 0; y < e.Height; y++)
        {
            for (int x = 0; x < e.Width; x++)
            {
                uint v;
                int at = y * rowBytes;
                switch (e.Psm)
                {
                    case 4: v = (uint)((lin[at + x / 2] >> ((x & 1) * 4)) & 15); break;
                    case 5: v = lin[at + x]; break;
                    case 6: v = BinaryPrimitives.ReadUInt16LittleEndian(lin.AsSpan(at + 2 * x)); break;
                    case 7: v = BinaryPrimitives.ReadUInt32LittleEndian(lin.AsSpan(at + 4 * x)); break;
                    case 3: v = BinaryPrimitives.ReadUInt32LittleEndian(lin.AsSpan(at + 4 * x)); break;
                    default: v = BinaryPrimitives.ReadUInt16LittleEndian(lin.AsSpan(at + 2 * x)); break;
                }
                uint rgbaPx;
                if (clut is not null)
                {
                    int i = (int)(((v >> e.ClutShift) & (uint)e.ClutMask) | (uint)(e.ClutStart << 4));
                    if (i >= clut.Length) throw new InvalidDataException($"PSP texture '{e.Name}' index {i} past its {clut.Length}-entry CLUT");
                    rgbaPx = clut[i];
                }
                else rgbaPx = Color(e.Psm, v);
                BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan((y * e.Width + x) * 4), rgbaPx);
            }
        }
        return rgba;
    }

    private static uint Color(int fmt, uint v)
    {
        uint r, g, b, a;
        switch (fmt)
        {
            case 0: r = v & 31; g = (v >> 5) & 63; b = (v >> 11) & 31; return 0xFF000000u | (E5(b) << 16) | (E6(g) << 8) | E5(r);
            case 1: r = v & 31; g = (v >> 5) & 31; b = (v >> 10) & 31; a = (v >> 15) * 255; return (a << 24) | (E5(b) << 16) | (E5(g) << 8) | E5(r);
            case 2: r = v & 15; g = (v >> 4) & 15; b = (v >> 8) & 15; a = (v >> 12) & 15; return (a * 17 << 24) | (b * 17 << 16) | (g * 17 << 8) | (r * 17);
            default: return v;
        }
        static uint E5(uint c) => c << 3 | c >> 2;
        static uint E6(uint c) => c << 2 | c >> 4;
    }

    private static byte[] Unswizzle(ReadOnlySpan<byte> s, int rowBytes, int height)
    {
        var d = new byte[s.Length];
        int blocksPerRow = rowBytes / 16, i = 0;
        for (int by = 0; by < (height + 7) / 8; by++)
            for (int bx = 0; bx < blocksPerRow; bx++)
                for (int y = 0; y < 8; y++, i += 16)
                {
                    int row = by * 8 + y;
                    if (row >= height || i + 16 > s.Length) continue;
                    s.Slice(i, 16).CopyTo(d.AsSpan(row * rowBytes + bx * 16));
                }
        return d;
    }

    public static string FormatName(int fmt) => fmt switch
    {
        0 => "BGR5650", 1 => "ABGR5551", 2 => "ABGR4444", 3 => "ABGR8888",
        4 => "T4 (CLUT)", 5 => "T8 (CLUT)", 6 => "T16 (CLUT)", 7 => "T32 (CLUT)",
        _ => $"0x{fmt:X2}",
    };
}
