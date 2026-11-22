using System.Buffers.Binary;
using System.Numerics;
using Omega.Plugins;
namespace SSNCR.Formats;

internal sealed record TrmFrame(int Parent, Matrix4x4 Local, uint NameHash);
internal sealed record TrmInstance(int Frame, int Atomic, uint Tint);
internal sealed record TrmMaterial(uint Rgba, string? Texture);
internal sealed record TrmStrip(int Material, int[] Corners, bool IsList);
internal sealed record TrmSkin(int Bones, byte[] Used, int MaxWeights, byte[] Indices, float[] Weights, Matrix4x4[] InverseBind);

internal sealed class TrmAtomic
{
    public uint Flags { get; init; }
    public int NumVerts { get; init; }
    public IReadOnlyList<TrmMaterial> Materials { get; init; } = [];
    public byte[] Attributes { get; init; } = [];
    public float[]? Positions { get; init; }
    public float[]? Normals { get; init; }
    public byte[]? Colors { get; init; }
    public float[]? Uv0 { get; init; }
    public IReadOnlyList<TrmStrip> Strips { get; init; } = [];
    public TrmSkin? Skin { get; init; }
}

internal sealed class TrmModel
{
    public bool BigEndian { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<TrmFrame> Frames { get; init; } = [];
    public IReadOnlyList<TrmInstance> Instances { get; init; } = [];
    public IReadOnlyList<TrmAtomic> Atomics { get; init; } = [];
    public int NumAnimations { get; init; }
    public int NumTracks { get; init; }
    public int NumKeys { get; init; }
    public int AnimationBytes { get; init; }
    public IReadOnlyList<TrmAnimation> Animations { get; init; } = [];
    // second hash table: name hashes of instances the game looks up by name
    public IReadOnlyDictionary<int, uint> InstanceNames { get; init; } = new Dictionary<int, uint>();
    // header +0x08: instances from this index on may take the game's distance-fade render types
    public int FadeFrom { get; init; }

    public Matrix4x4[] WorldMatrices()
    {
        var world = new Matrix4x4[Frames.Count];
        for (int i = 0; i < world.Length; i++)
        {
            int p = Frames[i].Parent;
            world[i] = p < 0 ? Frames[i].Local : Frames[i].Local * world[p];
        }
        return world;
    }
}

internal static partial class TrmFile
{
    private const int HeaderSize = 0x88;
    private const byte GxPos = 9, GxNrm = 10, GxClr0 = 11, GxTex0 = 13;

    public static bool IsTrm(ReadOnlySpan<byte> data)
        => Rw.IsChunk(data, 0, Rw.Struct)
            && data.Length >= 16
            && BinaryPrimitives.ReadUInt32BigEndian(data[12..]) is 0xBADA5535 or 0x3555DABA;

    public static TrmModel Read(byte[] data)
    {
        ReadOnlySpan<byte> d = data;
        if (!IsTrm(d)) throw new InvalidDataException("not a Torus .trm model");
        bool be = BinaryPrimitives.ReadUInt32BigEndian(d[12..]) == 0xBADA5535;
        int size = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        const int h = 0x10;
        if (size < HeaderSize || h + size > d.Length) throw new InvalidDataException("truncated .trm header");
        int U16(int o) => be ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + o)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(h + o));
        uint U32(int at) => be ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        float F32(int at) => BitConverter.UInt32BitsToSingle(U32(at));

        int numAtomics = U16(0x04), numInst = U16(0x06), numFrames = U16(0x0A);
        int numHash = U16(0x0C), numHash2 = U16(0x0E), numRecords = U16(0x18);
        int animBytes = (int)U32(h + 0x34), blobA = (int)U32(h + 0x3C);
        long expect = HeaderSize + 8L * numInst + 8L * numHash + 8L * numHash2 + 66L * numFrames
            + 0x54L * numRecords + blobA + animBytes;
        if (expect != size) throw new InvalidDataException($".trm header arrays total {expect} bytes, chunk says {size}");
        string name = Rw.CString(d.Slice(h + 0x48, 0x40));

        int p = h + HeaderSize;
        var inst = new TrmInstance[numInst];
        for (int i = 0; i < numInst; i++, p += 8)
        {
            int frame = be ? BinaryPrimitives.ReadUInt16BigEndian(d[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
            int atomic = be ? BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 2)..]);
            if (frame >= numFrames || atomic >= numAtomics) throw new InvalidDataException($"instance {i} points outside the model");
            inst[i] = new TrmInstance(frame, atomic, U32(p + 4));
        }
        var hashOf = new uint[numFrames];
        for (int i = 0; i < numHash; i++, p += 8)
        {
            uint frame = U32(p + 4);
            if (frame < numFrames) hashOf[frame] = U32(p);
        }
        var instanceNames = new Dictionary<int, uint>();
        for (int i = 0; i < numHash2; i++, p += 8)
        {
            uint index = U32(p + 4);
            if (index >= numInst) throw new InvalidDataException($"named instance {i} points outside the instances");
            instanceNames.TryAdd((int)index, U32(p));
        }
        var parents = new int[numFrames];
        for (int i = 0; i < numFrames; i++, p += 2)
        {
            parents[i] = (short)(be ? BinaryPrimitives.ReadUInt16BigEndian(d[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(d[p..]));
            if (parents[i] >= i) throw new InvalidDataException($"frame {i} has parent {parents[i]} after it");
        }
        var frames = new TrmFrame[numFrames];
        for (int i = 0; i < numFrames; i++, p += 64)
        {
            var m = new float[16];
            for (int k = 0; k < 16; k++) m[k] = F32(p + 4 * k);
            frames[i] = new TrmFrame(parents[i], MatrixOps.ToMatrix(m), hashOf[i]);
        }

        List<TrmAtomic> atomics = [];
        foreach (RwChunk c in Rw.Children(d, h + size, d.Length))
        {
            if (c.Type != Rw.Atomic) throw new InvalidDataException($"unexpected chunk 0x{c.Type:X} after the .trm header");
            atomics.Add(ReadAtomic(d, c));
        }
        if (atomics.Count != numAtomics) throw new InvalidDataException($"header lists {numAtomics} atomics, file has {atomics.Count}");

        return new TrmModel
        {
            BigEndian = be, Name = name, Frames = frames, Instances = inst, Atomics = atomics,
            NumAnimations = U16(0x10), NumTracks = U16(0x12), NumKeys = U16(0x14) + U16(0x16),
            AnimationBytes = animBytes, InstanceNames = instanceNames, FadeFrom = U16(0x08),
            Animations = TrmAnimReader.ReadIndexed(d.Slice(h + size - animBytes, animBytes), be, U16(0x10), U16(0x12), U16(0x14), U16(0x16)),
        };
    }

    private static TrmAtomic ReadAtomic(ReadOnlySpan<byte> d, RwChunk atomic)
    {
        RwChunk geo = Rw.Require(Rw.Children(d, atomic), Rw.Geometry);
        List<RwChunk> gs = Rw.Children(d, geo);
        RwChunk gst = Rw.Require(gs, Rw.Struct);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d[gst.Offset..]);
        int numVerts = BinaryPrimitives.ReadInt32LittleEndian(d[(gst.Offset + 8)..]);
        if ((flags & 0x01000000) == 0) throw new InvalidDataException("geometry is not platform-native");
        List<TrmMaterial> materials = ReadMaterials(d, Rw.Require(gs, Rw.MaterialList));
        List<RwChunk> ext = Rw.Children(d, Rw.Require(gs, Rw.Extension));

        RwChunk bin = Rw.Require(ext, Rw.BinMesh);
        int numMeshes = BinaryPrimitives.ReadInt32LittleEndian(d[(bin.Offset + 4)..]);
        if (bin.Size != 12 + 8 * numMeshes) throw new InvalidDataException("native binmesh carries index data");
        var meshMaterial = new int[numMeshes];
        for (int i = 0; i < numMeshes; i++)
        {
            meshMaterial[i] = BinaryPrimitives.ReadInt32LittleEndian(d[(bin.Offset + 16 + 8 * i)..]);
            if ((uint)meshMaterial[i] >= (uint)materials.Count) throw new InvalidDataException("binmesh material out of range");
        }

        RwChunk native = Rw.Require(ext, Rw.NativeData);
        if (native.Size < 16) throw new InvalidDataException("native geometry chunk is empty");
        uint platform = BinaryPrimitives.ReadUInt32LittleEndian(d[(native.Offset + 12)..]);
        if (platform == Rw.PlatformPs2)
            return ReadPs2Atomic(d, native, flags, numVerts, materials, meshMaterial, Rw.Find(ext, Rw.Skin));
        if (platform != Rw.PlatformGcn) throw new NotSupportedException($"native geometry for platform {platform} is not decoded");
        RwChunk ns = Rw.Require(Rw.Children(d, native), Rw.Struct);
        int hdrSize = BinaryPrimitives.ReadInt32LittleEndian(d[(ns.Offset + 4)..]);
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(d[(ns.Offset + 8)..]);
        int hp = ns.Offset + 12, data = hp + hdrSize;
        if (12 + hdrSize + dataSize != ns.Size) throw new InvalidDataException("native geometry sizes disagree");
        int numAttr = BinaryPrimitives.ReadInt32BigEndian(d[(hp + 8)..]);
        if (numAttr is < 1 or > 8 || hdrSize != 12 + 8 * numAttr + 8 * numMeshes)
            throw new InvalidDataException("native geometry header does not match its binmesh");

        var attr = new byte[numAttr];
        var wide = new bool[numAttr];
        var count = new int[numAttr];
        float[]? pos = null, nrm = null, uv0 = null;
        byte[]? clr = null;
        for (int a = 0; a < numAttr; a++)
        {
            int e = hp + 12 + 8 * a;
            int off = BinaryPrimitives.ReadInt32BigEndian(d[e..]);
            int end = a + 1 < numAttr ? BinaryPrimitives.ReadInt32BigEndian(d[(e + 8)..]) : dataSize;
            attr[a] = d[e + 4];
            int stride = d[e + 5];
            wide[a] = d[e + 6] switch
            {
                2 => false,
                3 => true,
                _ => throw new InvalidDataException($"GX attribute {attr[a]} uses index type {d[e + 6]}"),
            };
            if (stride == 0 || off < 0 || end > dataSize || end < off) throw new InvalidDataException("bad GX attribute array");
            count[a] = (end - off) / stride;
            ReadOnlySpan<byte> arr = d.Slice(data + off, count[a] * stride);
            switch (attr[a])
            {
                case GxPos when stride == 12: pos = BeFloats(arr); break;
                case GxNrm when stride == 12: nrm = BeFloats(arr); break;
                case GxClr0 when stride == 4: clr = arr.ToArray(); break;
                case GxTex0 when stride == 8: uv0 = BeFloats(arr); break;
            }
        }
        if (pos is null) throw new InvalidDataException("geometry has no position array");

        List<TrmStrip> strips = [];
        for (int m = 0; m < numMeshes; m++)
        {
            int e = hp + 12 + 8 * numAttr + 8 * m;
            int off = BinaryPrimitives.ReadInt32BigEndian(d[e..]);
            int len = BinaryPrimitives.ReadInt32BigEndian(d[(e + 4)..]);
            if (off < 0 || len < 0 || off + len > dataSize) throw new InvalidDataException("display list outside native data");
            ReadDisplayList(d.Slice(data + off, len), meshMaterial[m], wide, count, strips);
        }

        TrmSkin? skin = Rw.Find(ext, Rw.Skin) is { } sk ? ReadSkin(d, sk, numVerts) : null;
        if (skin is not null && pos.Length / 3 < numVerts) throw new InvalidDataException("skin covers more vertices than positions");
        return new TrmAtomic
        {
            Flags = flags, NumVerts = numVerts, Materials = materials, Attributes = attr,
            Positions = pos, Normals = nrm, Colors = clr, Uv0 = uv0, Strips = strips, Skin = skin,
        };
    }

    private static void ReadDisplayList(ReadOnlySpan<byte> dl, int material, bool[] wide, int[] count, List<TrmStrip> strips)
    {
        int n = wide.Length, p = 0;
        while (p < dl.Length)
        {
            byte op = dl[p];
            if (op == 0) { p++; continue; }
            if ((op & 0xF8) is not (0x90 or 0x98 or 0xA0)) throw new InvalidDataException($"GX opcode 0x{op:X2} in display list");
            if (p + 3 > dl.Length) throw new InvalidDataException("truncated GX primitive");
            int verts = BinaryPrimitives.ReadUInt16BigEndian(dl[(p + 1)..]);
            p += 3;
            var corners = new int[verts * n];
            for (int v = 0; v < verts; v++)
            {
                for (int a = 0; a < n; a++)
                {
                    int idx;
                    if (wide[a]) { idx = BinaryPrimitives.ReadUInt16BigEndian(dl[p..]); p += 2; }
                    else idx = dl[p++];
                    if (idx >= count[a]) throw new InvalidDataException($"GX index {idx} past attribute array of {count[a]}");
                    corners[v * n + a] = idx;
                }
            }
            if (p > dl.Length) throw new InvalidDataException("GX primitive overruns its display list");
            int kind = op & 0xF8;
            strips.Add(kind switch
            {
                0x98 => new TrmStrip(material, corners, false),
                0x90 => new TrmStrip(material, corners, true),
                _ => new TrmStrip(material, FanToList(corners, verts, n), true),
            });
        }
    }

    private static int[] FanToList(int[] c, int verts, int n)
    {
        List<int> tri = [];
        for (int i = 1; i + 1 < verts; i++)
            foreach (int v in new[] { 0, i, i + 1 })
                for (int a = 0; a < n; a++) tri.Add(c[v * n + a]);
        return [.. tri];
    }

    private static List<TrmMaterial> ReadMaterials(ReadOnlySpan<byte> d, RwChunk list)
    {
        List<RwChunk> ch = Rw.Children(d, list);
        RwChunk st = Rw.Require(ch, Rw.Struct);
        int n = BinaryPrimitives.ReadInt32LittleEndian(d[st.Offset..]);
        Queue<RwChunk> mats = new(ch.Where(c => c.Type == Rw.Material));
        List<TrmMaterial> result = [];
        for (int i = 0; i < n; i++)
        {
            int reuse = BinaryPrimitives.ReadInt32LittleEndian(d[(st.Offset + 4 + 4 * i)..]);
            if (reuse >= 0)
            {
                if (reuse >= result.Count) throw new InvalidDataException("material reuses a later entry");
                result.Add(result[reuse]);
                continue;
            }
            if (mats.Count == 0) throw new InvalidDataException("material list is short");
            List<RwChunk> mc = Rw.Children(d, mats.Dequeue());
            RwChunk ms = Rw.Require(mc, Rw.Struct);
            uint rgba = BinaryPrimitives.ReadUInt32BigEndian(d[(ms.Offset + 4)..]);
            bool textured = BinaryPrimitives.ReadInt32LittleEndian(d[(ms.Offset + 12)..]) != 0;
            string? tex = null;
            if (textured)
            {
                List<RwChunk> tc = Rw.Children(d, Rw.Require(mc, Rw.Texture));
                RwChunk nameChunk = tc.First(c => c.Type == Rw.String);
                tex = Rw.CString(d.Slice(nameChunk.Offset, nameChunk.Size));
            }
            result.Add(new TrmMaterial(rgba, tex));
        }
        return result;
    }

    private static TrmSkin ReadSkin(ReadOnlySpan<byte> d, RwChunk chunk, int numVerts)
    {
        RwChunk st = Rw.Require(Rw.Children(d, chunk), Rw.Struct);
        ReadOnlySpan<byte> s = d.Slice(st.Offset, st.Size);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != Rw.PlatformGcn) throw new NotSupportedException("non-GCN skin");
        int bones = s[4], used = s[5], mw = s[6];
        int need = 8 + used + 2 * numVerts * mw + 64 * bones + 12;
        if (mw is < 1 or > 4 || need != s.Length) throw new InvalidDataException($"skin is {s.Length} bytes, layout needs {need}");
        int p = 8;
        byte[] usedBones = s.Slice(p, used).ToArray(); p += used;
        byte[] idx = s.Slice(p, numVerts * mw).ToArray(); p += numVerts * mw;
        var w = new float[numVerts * mw];
        for (int i = 0; i < w.Length; i++) w[i] = s[p + i] / 128f;
        p += numVerts * mw;
        var inv = new Matrix4x4[bones];
        for (int b = 0; b < bones; b++, p += 64)
        {
            float[] m = BeFloats(s.Slice(p, 64));
            m[3] = m[7] = m[11] = 0f; m[15] = 1f;
            inv[b] = MatrixOps.ToMatrix(m);
        }
        foreach (byte b in idx) if (b >= bones) throw new InvalidDataException("skin bone index out of range");
        return new TrmSkin(bones, usedBones, mw, idx, w, inv);
    }

    private static float[] BeFloats(ReadOnlySpan<byte> s)
    {
        var f = new float[s.Length / 4];
        for (int i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleBigEndian(s[(4 * i)..]);
        return f;
    }
}
