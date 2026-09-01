using Kartz.Formats.Binary;
using System.Buffers.Binary;
namespace Kartz.Formats;

public sealed class SskDrawSection
{
    public int TextureIndex { get; init; }
    public int IndexFirst { get; init; }
    public int IndexCount { get; init; }
}
public sealed class SskTrack
{
    private SskTrack() { }
    public float[] Positions { get; private set; } = [];
    public float[] Normals { get; private set; } = [];
    public float[] Uvs { get; private set; } = [];
    public byte[] Colors { get; private set; } = [];
    public int[] Indices { get; private set; } = [];
    public SskDrawSection[] Sections { get; private set; } = [];
    public int[] TriangleChunkIndex { get; private set; } = [];
    public int[] TriangleBlockId { get; private set; } = [];
    public int[] TriangleBlockMode { get; private set; } = [];
    public IReadOnlyDictionary<(int Chunk, int Texture), bool[]> VertexTexcoordEmitted { get; private set; }
        = new Dictionary<(int, int), bool[]>();
    public int[] TriangleGroupId { get; private set; } = [];
    public int ChunkCount { get; private set; }
    public SskTexture[] Textures { get; private set; } = [];
    public int ReferencedSlotCount
    {
        get
        {
            int max = 0;
            foreach (SskDrawSection section in Sections)
            {
                if (section.TextureIndex >= max)
                {
                    max = section.TextureIndex + 1;
                }
            }
            return max;
        }
    }
    public void AttachTextures(IReadOnlyList<SskTexture> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        Textures = [.. textures];
    }
    public void AttachTextures(IReadOnlyList<DwkEntry> texEntries)
    {
        ArgumentNullException.ThrowIfNull(texEntries);
        Textures = [.. DecodeTextures(texEntries)];
    }
    public void AttachTextureSlots(IReadOnlyList<DwkEntry?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        List<SskTexture> textures = new(slots.Count);
        foreach (DwkEntry? slot in slots)
        {
            textures.Add(slot is not null && SskTexture.IsTexture(slot.Span)
                ? SskTexture.ParseForTrack(slot.Span, slot.Name)
                : new SskTexture());
        }
        Textures = [.. textures];
    }
    private static List<SskTexture> DecodeTextures(IReadOnlyList<DwkEntry> texEntries)
    {
        List<SskTexture> textures = new(texEntries.Count);
        foreach (DwkEntry e in texEntries)
        {
            textures.Add(SskTexture.IsTexture(e.Span) ? SskTexture.ParseForTrack(e.Span, e.Name) : new SskTexture());
        }
        return textures;
    }
    private static readonly Dictionary<byte, int> CmdParams = new()
    {
        [0x00] = 0,
        [0x10] = 1,
        [0x11] = 0,
        [0x12] = 1,
        [0x13] = 12,
        [0x14] = 16,
        [0x15] = 12,
        [0x16] = 9,
        [0x17] = 3,
        [0x18] = 1,
        [0x19] = 1,
        [0x1A] = 1,
        [0x1B] = 3,
        [0x1C] = 3,
        [0x20] = 1,
        [0x21] = 1,
        [0x22] = 1,
        [0x23] = 2,
        [0x24] = 1,
        [0x25] = 1,
        [0x26] = 1,
        [0x27] = 1,
        [0x28] = 1,
        [0x29] = 1,
        [0x2A] = 1,
        [0x2B] = 1,
        [0x30] = 1,
        [0x31] = 1,
        [0x32] = 1,
        [0x33] = 1,
        [0x34] = 32,
        [0x40] = 1,
        [0x41] = 0,
        [0x50] = 1,
        [0x60] = 1,
        [0x70] = 3,
        [0x71] = 2,
        [0x72] = 1,
    };
    private const byte CmdMtxIdentity = 0x11;
    private const byte CmdMtxScale = 0x1B;
    private const byte CmdMtxTrans = 0x1C;
    private const byte CmdColor = 0x20;
    private const byte CmdNormal = 0x21;
    private const byte CmdTexcoord = 0x22;
    private const byte CmdVtx16 = 0x23;
    private const byte CmdVtx10 = 0x24;
    private const byte CmdVtxXy = 0x25;
    private const byte CmdVtxXz = 0x26;
    private const byte CmdVtxYz = 0x27;
    private const byte CmdVtxDiff = 0x28;
    private const byte CmdTeximageParam = 0x2A;
    private const byte CmdBeginVtxs = 0x40;
    private const byte CmdEndVtxs = 0x41;
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<DwkEntry> texEntries)
    {
        return ParseInternal(blob, DecodeTextures(texEntries));
    }
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<SskTexture> textures)
    {
        return ParseInternal(blob, [.. textures]);
    }
    public static bool IsTrack(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 56 && BinaryRead.U32(blob, 0x0C) == 0x38;
    }
    private static SskTrack ParseInternal(ReadOnlySpan<byte> blob, List<SskTexture> textures)
    {
        int numTotal = BinaryRead.U16(blob, 0x02);
        int chunkTableStart = (int)BinaryRead.U32(blob, 0x0C);
        if (chunkTableStart < 0 || chunkTableStart + ((long)numTotal * 4) > blob.Length)
        {
            numTotal = 0;
        }
        List<float> allPos = [];
        List<float> allNrm = [];
        List<float> allUv = [];
        List<byte> allCol = [];
        List<int> triA = [];
        List<int> triB = [];
        List<int> triC = [];
        List<int> triTex = [];
        List<int> triChunk = [];
        List<int> triBlock = [];
        List<int> triBlockMode = [];
        List<int> triGroup = [];
        Dictionary<(int, int), List<bool>> texEmit = [];
        int blockCounter = 0;
        int groupCounter = 0;
        for (int ci = 0; ci < numTotal; ci++)
        {
            int chunkOff = (int)BinaryRead.U32(blob, chunkTableStart + (ci * 4));
            if (chunkOff < 0 || chunkOff + 24L > blob.Length)
            {
                continue;
            }
            DecodeChunk(blob, chunkOff, ci, allPos, allNrm, allUv, allCol, triA, triB, triC, triTex, triChunk,
                triBlock, triBlockMode, triGroup, texEmit, ref blockCounter, ref groupCounter);
        }
        SskTrack track = BuildTrack(allPos, allNrm, allUv, allCol, triA, triB, triC, triTex, triChunk,
            triBlock, triBlockMode, triGroup, numTotal, textures);
        track.VertexTexcoordEmitted = texEmit.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        return track;
    }
    private static SskTrack BuildTrack(
        List<float> allPos, List<float> allNrm, List<float> allUv, List<byte> allCol,
        List<int> triA, List<int> triB, List<int> triC, List<int> triTex, List<int> triChunk,
        List<int> triBlock, List<int> triBlockMode, List<int> triGroup, int chunkCount, List<SskTexture> textures)
    {
        int triCount = triA.Count;
        int[] order = Enumerable.Range(0, triCount).OrderBy(i => triChunk[i]).ToArray();
        int[] flatIdx = new int[triCount * 3];
        int[] flatChunk = new int[triCount];
        int[] flatBlock = new int[triCount];
        int[] flatBlockMode = new int[triCount];
        int[] flatGroup = new int[triCount];
        List<SskDrawSection> sections = [];
        int prevSlot = int.MinValue;
        int secStart = 0;
        for (int i = 0; i < triCount; i++)
        {
            int t = order[i];
            int slot = triTex[t];
            flatIdx[i * 3] = triA[t];
            flatIdx[(i * 3) + 1] = triB[t];
            flatIdx[(i * 3) + 2] = triC[t];
            flatChunk[i] = triChunk[t];
            flatBlock[i] = triBlock[t];
            flatBlockMode[i] = triBlockMode[t];
            flatGroup[i] = triGroup[t];
            if (slot != prevSlot)
            {
                if (prevSlot != int.MinValue)
                {
                    sections.Add(new SskDrawSection
                    {
                        TextureIndex = prevSlot,
                        IndexFirst = secStart,
                        IndexCount = (i * 3) - secStart,
                    });
                }
                secStart = i * 3;
                prevSlot = slot;
            }
        }
        if (triCount > 0)
        {
            sections.Add(new SskDrawSection
            {
                TextureIndex = prevSlot,
                IndexFirst = secStart,
                IndexCount = (triCount * 3) - secStart,
            });
        }
        return new SskTrack
        {
            Positions = [.. allPos],
            Normals = [.. allNrm],
            Uvs = [.. allUv],
            Colors = [.. allCol],
            Indices = flatIdx,
            Sections = [.. sections],
            Textures = [.. textures],
            TriangleChunkIndex = flatChunk,
            TriangleBlockId = flatBlock,
            TriangleBlockMode = flatBlockMode,
            TriangleGroupId = flatGroup,
            ChunkCount = chunkCount,
        };
    }
    private static void DecodeChunk(
        ReadOnlySpan<byte> blob, int chunkOff, int chunkIndex,
        List<float> allPos, List<float> allNrm, List<float> allUv, List<byte> allCol,
        List<int> triA, List<int> triB, List<int> triC, List<int> triTex, List<int> triChunk,
        List<int> triBlock, List<int> triBlockMode, List<int> triGroup,
        Dictionary<(int, int), List<bool>> texEmit,
        ref int blockCounter, ref int groupCounter)
    {
        uint texCount = BinaryRead.U32(blob, chunkOff + 0x10);
        uint dlSize = BinaryRead.U32(blob, chunkOff + 0x04);
        long headerSize = 24 + ((long)texCount * 12);
        long dlOff = chunkOff + headerSize;
        if (dlSize == 0 || dlOff + dlSize > blob.Length)
        {
            return;
        }
        Dictionary<int, int> texMap = [];
        for (int ti = 0; ti < (int)texCount; ti++)
        {
            int entBase = chunkOff + 24 + (ti * 12);
            int dlByteOff = (int)BinaryRead.U32(blob, entBase + 0);
            int idx1 = (int)BinaryRead.U32(blob, entBase + 8);
            texMap[dlByteOff] = idx1 - 1;
        }
        byte[] dlArr = blob.Slice((int)dlOff, (int)dlSize).ToArray();
        int dlWords = dlArr.Length / 4;
        float cx = 0, cy = 0, cz = 0;
        float nx = 0, ny = 0, nz = 0;
        float tu = 0, tv = 0;
        byte cr = 255, cg = 255, cb = 255;
        float sx = 1, sy = 1, sz = 1;
        float tx = 0, ty = 0, tz = 0;
        int curTex = -1;
        int mode = 0;
        List<int> strip = new(64);
        int word = 0;
        int curBlock = -1;
        int curGroup = -1;
        blockCounter++;
        groupCounter++;
        uint ReadW()
        {
            return word < dlWords
            ? BinaryPrimitives.ReadUInt32LittleEndian(dlArr.AsSpan(word++ * 4))
            : 0u;
        }
        bool pendingTexcoord = false;
        void EmitVtx()
        {
            allPos.Add((cx * sx) + tx); allPos.Add((cy * sy) + ty); allPos.Add((cz * sz) + tz);
            allNrm.Add(nx); allNrm.Add(ny); allNrm.Add(nz);
            allUv.Add(tu); allUv.Add(tv);
            allCol.Add(cr); allCol.Add(cg); allCol.Add(cb); allCol.Add(255);
            strip.Add((allPos.Count / 3) - 1);
            if (!texEmit.TryGetValue((chunkIndex, curTex), out List<bool>? flags))
            {
                texEmit[(chunkIndex, curTex)] = flags = [];
            }
            flags.Add(pendingTexcoord);
            pendingTexcoord = false;
        }
        void Tri(int a, int b, int c)
        {
            triA.Add(a); triB.Add(b); triC.Add(c);
            triTex.Add(curTex); triChunk.Add(chunkIndex);
            triBlock.Add(curBlock); triBlockMode.Add(mode); triGroup.Add(curGroup);
        }
        while (word < dlWords)
        {
            uint pack = ReadW();
            for (int packed = 0; packed < 4; packed++)
            {
                byte cmd = (byte)((pack >> (packed * 8)) & 0xFF);
                int paramByteOff = word * 4;
                int np = CmdParams.TryGetValue(cmd, out int n) ? n : 0;
                uint p0 = np >= 1 ? ReadW() : 0u;
                uint p1 = np >= 2 ? ReadW() : 0u;
                uint p2 = np >= 3 ? ReadW() : 0u;
                for (int pi = 3; pi < np; pi++) { _ = ReadW(); }
                switch (cmd)
                {
                    case CmdMtxIdentity:
                        sx = sy = sz = 1; tx = ty = tz = 0; break;
                    case CmdMtxTrans:
                        tx = (int)p0 / 4096f; ty = (int)p1 / 4096f; tz = (int)p2 / 4096f;
                        break;
                    case CmdMtxScale:
                        sx = (int)p0 / 4096f; sy = (int)p1 / 4096f; sz = (int)p2 / 4096f;
                        break;
                    case CmdColor:
                        {
                            uint c = p0 & 0x7FFF;
                            cr = Exp5(c & 0x1F); cg = Exp5((c >> 5) & 0x1F); cb = Exp5((c >> 10) & 0x1F);
                            break;
                        }
                    case CmdNormal:
                        nx = S10(p0 & 0x3FF) / 512f;
                        ny = S10((p0 >> 10) & 0x3FF) / 512f;
                        nz = S10((p0 >> 20) & 0x3FF) / 512f;
                        break;
                    case CmdTexcoord:
                        tu = (short)(p0 & 0xFFFF) / 16f;
                        tv = (short)((p0 >> 16) & 0xFFFF) / 16f;
                        pendingTexcoord = true;
                        break;
                    case CmdVtx16:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cy = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        cz = (short)(p1 & 0xFFFF) / 4096f;
                        EmitVtx(); break;
                    case CmdVtx10:
                        cx = S10(p0 & 0x3FF) / 64f;
                        cy = S10((p0 >> 10) & 0x3FF) / 64f;
                        cz = S10((p0 >> 20) & 0x3FF) / 64f;
                        EmitVtx(); break;
                    case CmdVtxXy:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cy = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        EmitVtx(); break;
                    case CmdVtxXz:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cz = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        EmitVtx(); break;
                    case CmdVtxYz:
                        cy = (short)(p0 & 0xFFFF) / 4096f;
                        cz = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        EmitVtx(); break;
                    case CmdVtxDiff:
                        cx += S10(p0 & 0x3FF) / 4096f;
                        cy += S10((p0 >> 10) & 0x3FF) / 4096f;
                        cz += S10((p0 >> 20) & 0x3FF) / 4096f;
                        EmitVtx(); break;
                    case CmdTeximageParam:
                        curGroup = ++groupCounter;
                        if (texMap.TryGetValue(paramByteOff, out int slot)) { curTex = slot; }
                        break;
                    case CmdBeginVtxs:
                        mode = (int)(p0 & 0x3); strip.Clear(); curBlock = ++blockCounter; break;
                    case CmdEndVtxs: break;
                }
                if (cmd is CmdVtx16 or CmdVtx10 or CmdVtxXy or CmdVtxXz or CmdVtxYz or CmdVtxDiff)
                {
                    int n2 = strip.Count;
                    switch (mode)
                    {
                        case 0:
                            if (n2 % 3 == 0 && n2 >= 3)
                            {
                                Tri(strip[n2 - 3], strip[n2 - 2], strip[n2 - 1]);
                            }
                            break;
                        case 1:
                            if (n2 % 4 == 0 && n2 >= 4)
                            {
                                Tri(strip[n2 - 4], strip[n2 - 3], strip[n2 - 2]);
                                Tri(strip[n2 - 4], strip[n2 - 2], strip[n2 - 1]);
                            }
                            break;
                        case 2:
                            if (n2 >= 3)
                            {
                                if ((n2 - 1) % 2 == 0)
                                { Tri(strip[n2 - 3], strip[n2 - 2], strip[n2 - 1]); }
                                else
                                { Tri(strip[n2 - 2], strip[n2 - 3], strip[n2 - 1]); }
                            }
                            break;
                        case 3:
                            if (n2 >= 4 && n2 % 2 == 0)
                            {
                                Tri(strip[n2 - 4], strip[n2 - 3], strip[n2 - 1]);
                                Tri(strip[n2 - 4], strip[n2 - 1], strip[n2 - 2]);
                            }
                            break;
                    }
                }
            }
        }
    }
    private static int S10(uint v)
    {
        return (v & 0x200) != 0 ? (int)v | unchecked((int)0xFFFFFC00) : (int)v;
    }
    private static byte Exp5(uint v)
    {
        byte b = (byte)(v << 3);
        return (byte)(b | (b >> 5));
    }
}
