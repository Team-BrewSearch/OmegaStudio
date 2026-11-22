using System.Buffers.Binary;
using System.Numerics;
using Omega.Plugins;
namespace SSNCR.Formats;

internal static partial class TrmFile
{
    private const int V2_32 = 0x4, V3_32 = 0x8, V3_8 = 0xA, V4_32 = 0xC, V4_8 = 0xE;

    private sealed class Ps2Batch
    {
        public uint[]? Pos;
        public int PosComps;
        public uint[]? Uv;
        public sbyte[]? Nrm;
        public byte[]? Clr;
        public uint[]? Wts;
        public int Count = -1;
    }

    private static TrmAtomic ReadPs2Atomic(ReadOnlySpan<byte> d, RwChunk native, uint flags, int numVerts,
        List<TrmMaterial> materials, int[] meshMaterial, RwChunk? skinChunk)
    {
        int p = native.Offset + 16, end = native.End;
        List<float> pos = [], nrm = [], uv = [], wts = [];
        List<byte> clr = [], jnt = [];
        List<TrmStrip> strips = [];
        bool hasN = (flags & 0x10) != 0, hasC = (flags & 0x08) != 0, hasT = (flags & 0x04) != 0, skinned = skinChunk is not null;
        foreach (int material in meshMaterial)
        {
            if (p + 8 > end) throw new InvalidDataException("PS2 mesh table is short");
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[p..]);
            if (size < 0 || p + 8 + size > end) throw new InvalidDataException("PS2 mesh overruns its chunk");
            List<int> tris = [];
            foreach (Ps2Batch b in Ps2Batches(d.Slice(p + 8, size)))
            {
                int n = b.Count;
                if (b.Pos is null || n < 0 || b.Pos.Length < n * b.PosComps) throw new InvalidDataException("PS2 batch has no positions");
                if ((hasT && (b.Uv is null || b.Uv.Length < 2 * n)) || (skinned && (b.Wts is null || b.Wts.Length < 4 * n)))
                    throw new InvalidDataException("PS2 batch is missing an attribute");
                int first = pos.Count / 3;
                for (int v = 0; v < n; v++)
                {
                    for (int k = 0; k < 3; k++) pos.Add(BitConverter.UInt32BitsToSingle(b.Pos[v * b.PosComps + k]));
                    if (hasT) { uv.Add(BitConverter.UInt32BitsToSingle(b.Uv![2 * v])); uv.Add(BitConverter.UInt32BitsToSingle(b.Uv[2 * v + 1])); }
                    if (hasN)
                    {
                        var nv = b.Nrm is { } nn && nn.Length >= 3 * n ? new Vector3(nn[3 * v], nn[3 * v + 1], nn[3 * v + 2]) / 127f : Vector3.Zero;
                        nrm.Add(nv.X); nrm.Add(nv.Y); nrm.Add(nv.Z);
                    }
                    if (hasC)
                    {
                        if (b.Clr is { } cc && cc.Length >= 4 * n) for (int k = 0; k < 4; k++) clr.Add(cc[4 * v + k]);
                        else { clr.Add(255); clr.Add(255); clr.Add(255); clr.Add(255); }
                    }
                    if (skinned)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            uint w = b.Wts![4 * v + k];
                            int slot = (int)((w & 0x3FF) >> 2);
                            float wt = BitConverter.UInt32BitsToSingle(w & ~0x3FFu);
                            if (slot == 0 && wt != 0f) throw new InvalidDataException("PS2 skin weight uses matrix slot 0");
                            jnt.Add((byte)Math.Max(slot - 1, 0));
                            wts.Add(slot == 0 ? 0f : wt);
                        }
                    }
                }
                for (int v = 2; v < n; v++)
                {
                    if (b.PosComps == 4 && (b.Pos[v * 4 + 3] & 0x8000) != 0) continue;
                    int a = first + v - 2, c = first + v - 1, e = first + v;
                    if (Same(pos, a, c) || Same(pos, c, e) || Same(pos, a, e)) continue;
                    if ((v & 1) == 0) { tris.Add(a); tris.Add(c); tris.Add(e); }
                    else { tris.Add(c); tris.Add(a); tris.Add(e); }
                }
            }
            strips.Add(new TrmStrip(material, [.. tris.SelectMany(i => Enumerable.Repeat(i, 1 + (hasN ? 1 : 0) + (hasC ? 1 : 0) + (hasT ? 1 : 0)))], true));
            p += 8 + size;
        }
        if (p != end) throw new InvalidDataException("PS2 native geometry has trailing data");

        List<byte> attrs = [9];
        if (hasN) attrs.Add(10);
        if (hasC) attrs.Add(11);
        if (hasT) attrs.Add(13);
        TrmSkin? skin = null;
        if (skinChunk is { } sc)
        {
            (int bones, byte[] used, Matrix4x4[] inv) = ReadPs2Skin(d, sc);
            byte[] idx = [.. jnt];
            foreach (byte j in idx) if (j >= bones) throw new InvalidDataException("PS2 skin bone index out of range");
            skin = new TrmSkin(bones, used, 4, idx, [.. wts], inv);
        }
        return new TrmAtomic
        {
            Flags = flags, NumVerts = numVerts, Materials = materials, Attributes = [.. attrs],
            Positions = [.. pos], Normals = hasN ? [.. nrm] : null, Colors = hasC ? [.. clr] : null,
            Uv0 = hasT ? [.. uv] : null, Strips = strips, Skin = skin,
        };
    }

    private static bool Same(List<float> pos, int a, int b)
        => pos[3 * a] == pos[3 * b] && pos[3 * a + 1] == pos[3 * b + 1] && pos[3 * a + 2] == pos[3 * b + 2];

    private static List<Ps2Batch> Ps2Batches(ReadOnlySpan<byte> mesh)
    {
        List<Ps2Batch> batches = [];
        var cur = new Ps2Batch();
        int p = 0;
        while (true)
        {
            if (p + 16 > mesh.Length) throw new InvalidDataException("PS2 DMA chain runs off its mesh");
            ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(mesh[p..]);
            int qwc = (int)(tag & 0xFFFF), id = (int)((tag >> 28) & 7);
            long addr = (long)((tag >> 32) & 0x7FFFFFFF) * 16;
            byte[] stream;
            switch (id)
            {
                case 3:
                    if (addr + qwc * 16L > mesh.Length) throw new InvalidDataException("PS2 DMA ref outside its mesh");
                    stream = [.. mesh.Slice(p + 8, 8), .. mesh.Slice((int)addr, qwc * 16)];
                    p += 16;
                    break;
                case 1 or 6 or 7:
                    if (p + 16 + qwc * 16 > mesh.Length) throw new InvalidDataException("PS2 DMA packet overruns its mesh");
                    stream = mesh.Slice(p + 8, 8 + qwc * 16).ToArray();
                    p += 16 + qwc * 16;
                    break;
                default:
                    throw new InvalidDataException($"PS2 DMA tag id {id}");
            }
            RunVif(stream, ref cur, batches);
            if (id is 6 or 7) break;
        }
        return batches;
    }

    private static void RunVif(ReadOnlySpan<byte> s, ref Ps2Batch cur, List<Ps2Batch> batches)
    {
        int q = 0;
        while (q + 4 <= s.Length)
        {
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(s[q..]);
            int cmd = (int)(code >> 24) & 0x7F, num = (int)(code >> 16) & 0xFF, imm = (int)(code & 0xFFFF);
            q += 4;
            if (cmd >= 0x60)
            {
                int fmt = cmd & 0xF, n = num == 0 ? 256 : num;
                int bytes = fmt switch { V2_32 => 8, V3_32 => 12, V3_8 => 3, V4_32 => 16, V4_8 => 4, _ => throw new InvalidDataException($"VIF unpack format 0x{fmt:X}") } * n;
                if ((cmd & 0x10) != 0) throw new InvalidDataException("masked VIF unpack");
                if (q + bytes > s.Length) throw new InvalidDataException("VIF unpack overruns its packet");
                ReadOnlySpan<byte> data = s.Slice(q, bytes);
                int slot = imm & 0x3FF;
                switch (fmt)
                {
                    case V3_32 or V4_32 when slot == 0: cur.Pos = Words(data); cur.PosComps = fmt == V4_32 ? 4 : 3; break;
                    case V4_32: cur.Wts = Words(data); break;
                    case V2_32: cur.Uv = Words(data); break;
                    case V3_8: cur.Nrm = [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(data)]; break;
                    case V4_8: cur.Clr = data.ToArray(); break;
                    default: throw new InvalidDataException($"VIF unpack 0x{fmt:X} at slot {slot}");
                }
                q += (bytes + 3) & ~3;
                continue;
            }
            switch (cmd)
            {
                case 0x04: cur.Count = imm & 0x3FF; break;
                case 0x14 or 0x15 or 0x17: batches.Add(cur); cur = new Ps2Batch(); break;
                case 0x20: q += 4; break;
                case 0x30 or 0x31: q += 16; break;
                case 0x50 or 0x51: q += imm * 16; break;
                case 0x4A: q += (num == 0 ? 256 : num) * 8; break;
            }
        }
    }

    private static uint[] Words(ReadOnlySpan<byte> s)
    {
        var w = new uint[s.Length / 4];
        for (int i = 0; i < w.Length; i++) w[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(4 * i)..]);
        return w;
    }

    private static (int Bones, byte[] Used, Matrix4x4[] Inverse) ReadPs2Skin(ReadOnlySpan<byte> d, RwChunk chunk)
    {
        RwChunk st = Rw.Require(Rw.Children(d, chunk), Rw.Struct);
        ReadOnlySpan<byte> s = d.Slice(st.Offset, st.Size);
        if (s.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(s) != Rw.PlatformPs2) throw new InvalidDataException("PS2 skin has the wrong platform");
        int bones = s[4], used = s[5];
        if (8 + used + 64 * bones + 28 != s.Length) throw new InvalidDataException($"PS2 skin is {s.Length} bytes");
        int p = 8 + used;
        var inv = new Matrix4x4[bones];
        for (int b = 0; b < bones; b++, p += 64)
        {
            var m = new float[16];
            for (int k = 0; k < 16; k++) m[k] = BinaryPrimitives.ReadSingleLittleEndian(s[(p + 4 * k)..]);
            m[3] = m[7] = m[11] = 0f; m[15] = 1f;
            inv[b] = MatrixOps.ToMatrix(m);
        }
        return (bones, s.Slice(8, used).ToArray(), inv);
    }
}
