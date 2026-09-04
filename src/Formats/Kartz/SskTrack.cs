using Kartz.Formats.Binary;
using Kartz.Formats.Gx;
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
    public int[] VertexSlots { get; private set; } = [];
    public int[] VertexChunks { get; private set; } = [];
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
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<DwkEntry> texEntries)
    {
        return ParseInternal(blob, DecodeTextures(texEntries));
    }
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<SskTexture> textures)
    {
        return ParseInternal(blob, [.. textures]);
    }
    private const int CountField = 0x02;
    private static readonly int[] TableFields = [0x0C, 0x1C];
    public void BindVerticesToChunkBones(int[] chunkBones, int slotBase)
    {
        ArgumentNullException.ThrowIfNull(chunkBones);
        int[] slots = new int[VertexChunks.Length];
        for (int vertex = 0; vertex < slots.Length; vertex++)
        {
            int chunk = VertexChunks[vertex];
            slots[vertex] = chunk >= 0 && chunk < chunkBones.Length ? chunkBones[chunk] + slotBase : -1;
        }
        VertexSlots = slots;
    }
    public static bool IsTrack(ReadOnlySpan<byte> blob)
    {
        return TableOf(blob).Table >= 0;
    }
    public static int ChunkTableOffset(ReadOnlySpan<byte> blob)
    {
        return TableOf(blob).Table;
    }
    public static (int Table, int Count) ChunkTable(ReadOnlySpan<byte> blob)
    {
        return TableOf(blob);
    }
    private static (int Table, int Count) TableOf(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 0x20)
        {
            return (-1, 0);
        }
        int count = BinaryRead.U16(blob, CountField);
        if (count > 0)
        {
            foreach (int field in TableFields)
            {
                if (field + 4 > blob.Length)
                {
                    continue;
                }
                long table = BinaryRead.U32(blob, field);
                if (Fits(blob, table, count))
                {
                    return ((int)table, count);
                }
            }
        }
        long wide = BinaryRead.U32(blob, 0);
        long wideTable = BinaryRead.U32(blob, 4);
        if (wide is > 0 and <= 8192
            && BinaryRead.U32(blob, 8) == wideTable + (wide * 4)
            && Fits(blob, wideTable, (int)wide))
        {
            return ((int)wideTable, (int)wide);
        }
        return (-1, 0);
    }
    private static bool Fits(ReadOnlySpan<byte> blob, long table, int count)
    {
        long tableEnd = table + ((long)count * 4);
        if (table < 0x20 || tableEnd > blob.Length)
        {
            return false;
        }
        long first = tableEnd + ((long)count * 12);
        if (first > blob.Length || BinaryRead.U32(blob, (int)table) != first)
        {
            return false;
        }
        for (int chunk = 0; chunk < count; chunk++)
        {
            long at = BinaryRead.U32(blob, (int)table + (chunk * 4));
            if (at == 0)
            {
                continue;
            }
            if (at < tableEnd || ChunkLayout.Of(blob, at) is null)
            {
                return false;
            }
        }
        return true;
    }
    public static SskTrack ParseChunks(
        ReadOnlySpan<byte> blob, IReadOnlyList<int> chunkOffsets, IReadOnlyList<SskTexture> textures)
    {
        ArgumentNullException.ThrowIfNull(chunkOffsets);
        ArgumentNullException.ThrowIfNull(textures);
        return Assemble(blob, chunkOffsets, [.. textures]);
    }
    private static SskTrack ParseInternal(ReadOnlySpan<byte> blob, List<SskTexture> textures)
    {
        (int chunkTableStart, int numTotal) = TableOf(blob);
        if (chunkTableStart < 0)
        {
            numTotal = 0;
            chunkTableStart = 0;
        }
        List<int> offsets = new(numTotal);
        for (int ci = 0; ci < numTotal; ci++)
        {
            offsets.Add((int)BinaryRead.U32(blob, chunkTableStart + (ci * 4)));
        }
        return Assemble(blob, offsets, textures);
    }
    private static SskTrack Assemble(
        ReadOnlySpan<byte> blob, IReadOnlyList<int> chunkOffsets, List<SskTexture> textures)
    {
        List<float> allPos = [];
        List<float> allNrm = [];
        List<float> allUv = [];
        List<int> allSlot = [];
        List<int> allVertexChunk = [];
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
        for (int ci = 0; ci < chunkOffsets.Count; ci++)
        {
            int chunkOff = chunkOffsets[ci];
            if (chunkOff < 0 || chunkOff + 24L > blob.Length)
            {
                continue;
            }
            int before = allPos.Count / 3;
            DecodeChunk(blob, chunkOff, ci, Shapes(blob, chunkOffsets, ci),
                allPos, allNrm, allUv, allCol, allSlot, triA, triB, triC, triTex, triChunk,
                triBlock, triBlockMode, triGroup, texEmit, ref blockCounter, ref groupCounter);
            for (int added = before; added < allPos.Count / 3; added++)
            {
                allVertexChunk.Add(ci);
            }
        }
        SskTrack track = BuildTrack(allPos, allNrm, allUv, allCol, triA, triB, triC, triTex, triChunk,
            triBlock, triBlockMode, triGroup, chunkOffsets.Count, textures);
        track.VertexSlots = [.. allSlot];
        track.VertexChunks = [.. allVertexChunk];
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
    private static ChunkLayout? Shapes(ReadOnlySpan<byte> blob, IReadOnlyList<int> offsets, int index)
    {
        long wanted = -1;
        foreach (int candidate in offsets)
        {
            if (candidate > offsets[index] && (wanted < 0 || candidate < wanted))
            {
                wanted = candidate;
            }
        }
        return ChunkLayout.Of(blob, offsets[index], wanted) ?? ChunkLayout.Of(blob, offsets[index]);
    }
    private static void DecodeChunk(
        ReadOnlySpan<byte> blob, int chunkOff, int chunkIndex, ChunkLayout? shape,
        List<float> allPos, List<float> allNrm, List<float> allUv, List<byte> allCol, List<int> allSlot,
        List<int> triA, List<int> triB, List<int> triC, List<int> triTex, List<int> triChunk,
        List<int> triBlock, List<int> triBlockMode, List<int> triGroup,
        Dictionary<(int, int), List<bool>> texEmit,
        ref int blockCounter, ref int groupCounter)
    {
        if (shape is not { } layout)
        {
            return;
        }
        uint texCount = BinaryRead.U32(blob, chunkOff + layout.TextureCountField);
        long dlSize = BinaryRead.U32(blob, chunkOff + 0x04) - layout.PrefixBytes(blob, chunkOff);
        long dlOff = layout.ListStart(blob, chunkOff);
        if (dlSize <= 0 || dlOff + dlSize > blob.Length)
        {
            return;
        }
        Dictionary<int, int> texMap = [];
        for (int ti = 0; ti < (int)texCount; ti++)
        {
            int entBase = chunkOff + layout.HeaderSize + (ti * layout.TextureRefSize);
            int dlByteOff = (int)BinaryRead.U32(blob, entBase + 0);
            int idx1 = (int)BinaryRead.U32(blob, entBase + 8);
            texMap[dlByteOff] = idx1 > 0 ? idx1 - 1 : 0;
        }
        blockCounter++;
        groupCounter++;
        TrackSink sink = new(
            chunkIndex, texMap, allPos, allNrm, allUv, allCol, allSlot,
            triA, triB, triC, triTex, triChunk, triBlock, triBlockMode, triGroup, texEmit,
            blockCounter, groupCounter);
        GxRunner.Run(blob.Slice((int)dlOff, (int)dlSize), sink);
        blockCounter = sink.BlockCounter;
        groupCounter = sink.GroupCounter;
    }
    private sealed class TrackSink : IGxSink
    {
        private readonly int _chunkIndex;
        private readonly Dictionary<int, int> _texMap;
        private readonly List<float> _pos;
        private readonly List<float> _nrm;
        private readonly List<float> _uv;
        private readonly List<int> _slots;
        private readonly List<byte> _col;
        private readonly List<int> _triA;
        private readonly List<int> _triB;
        private readonly List<int> _triC;
        private readonly List<int> _triTex;
        private readonly List<int> _triChunk;
        private readonly List<int> _triBlock;
        private readonly List<int> _triBlockMode;
        private readonly List<int> _triGroup;
        private readonly Dictionary<(int, int), List<bool>> _texEmit;
        private int _texture = -1;
        private int _slot = -1;
        private int _block = -1;
        private int _group = -1;
        public TrackSink(
            int chunkIndex, Dictionary<int, int> texMap,
            List<float> pos, List<float> nrm, List<float> uv, List<byte> col, List<int> slots,
            List<int> triA, List<int> triB, List<int> triC, List<int> triTex, List<int> triChunk,
            List<int> triBlock, List<int> triBlockMode, List<int> triGroup,
            Dictionary<(int, int), List<bool>> texEmit, int blockCounter, int groupCounter)
        {
            _chunkIndex = chunkIndex;
            _texMap = texMap;
            _pos = pos;
            _nrm = nrm;
            _uv = uv;
            _col = col;
            _slots = slots;
            _triA = triA;
            _triB = triB;
            _triC = triC;
            _triTex = triTex;
            _triChunk = triChunk;
            _triBlock = triBlock;
            _triBlockMode = triBlockMode;
            _triGroup = triGroup;
            _texEmit = texEmit;
            BlockCounter = blockCounter;
            GroupCounter = groupCounter;
        }
        public int BlockCounter { get; private set; }
        public int GroupCounter { get; private set; }
        public void Begin(int mode)
        {
            _block = ++BlockCounter;
        }
        public void MatrixRestore(int slot)
        {
            _slot = slot;
        }
        public void TextureParam(uint param, int byteOffset)
        {
            _group = ++GroupCounter;
            if (_texMap.TryGetValue(byteOffset, out int slot))
            {
                _texture = slot;
            }
        }
        public int Vertex(GxVertex vertex)
        {
            _pos.Add(vertex.X);
            _pos.Add(vertex.Y);
            _pos.Add(vertex.Z);
            _nrm.Add(vertex.Nx);
            _nrm.Add(vertex.Ny);
            _nrm.Add(vertex.Nz);
            _uv.Add(vertex.U);
            _uv.Add(vertex.V);
            _slots.Add(_slot);
            _col.Add(vertex.R);
            _col.Add(vertex.G);
            _col.Add(vertex.B);
            _col.Add(255);
            if (!_texEmit.TryGetValue((_chunkIndex, _texture), out List<bool>? flags))
            {
                _texEmit[(_chunkIndex, _texture)] = flags = [];
            }
            flags.Add(vertex.Textured);
            return (_pos.Count / 3) - 1;
        }
        public void Triangle(int a, int b, int c, int mode)
        {
            _triA.Add(a);
            _triB.Add(b);
            _triC.Add(c);
            _triTex.Add(_texture);
            _triChunk.Add(_chunkIndex);
            _triBlock.Add(_block);
            _triBlockMode.Add(mode);
            _triGroup.Add(_group);
        }
    }
}
