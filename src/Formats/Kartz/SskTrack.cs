using Kartz.Formats.Binary;
using System.Buffers.Binary;
using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// A contiguous run of triangles that share the same texture slot.
/// Used by the renderer and glTF exporter to batch draw calls.
/// </summary>
public sealed class SskDrawSection
{
    /// <summary>0-based index into <see cref="SskTrack.Textures"/>. -1 = untextured.</summary>
    public int TextureIndex { get; init; }
    /// <summary>First index into <see cref="SskTrack.Indices"/>.</summary>
    public int IndexFirst { get; init; }
    /// <summary>Number of indices (always a multiple of 3).</summary>
    public int IndexCount { get; init; }
}

/// <summary>
/// A decoded TRK track model: world-space vertex attributes plus indexed triangles
/// grouped into draw sections by texture.
///
/// <para>
/// Vertex arrays are parallel: element <c>i</c> of Positions/Normals/Uvs/Colors all
/// describe the same vertex.  Positions are world-space floats; UVs are in pixel
/// space — divide by <see cref="SskTexture.Width"/> and <see cref="SskTexture.Height"/>
/// before uploading to GL or glTF.
/// </para>
/// </summary>
public sealed class SskTrack
{
    private SskTrack() { }

    /// <summary>World-space positions, xyz triples (3 floats per vertex).</summary>
    public float[] Positions { get; private set; } = [];

    /// <summary>Normals, xyz triples (3 floats per vertex).</summary>
    public float[] Normals { get; private set; } = [];

    /// <summary>UV coordinates in pixel space, uv pairs (2 floats per vertex).</summary>
    public float[] Uvs { get; private set; } = [];

    /// <summary>Vertex colors, RGBA bytes (4 bytes per vertex).</summary>
    public byte[] Colors { get; private set; } = [];

    /// <summary>Flat triangle index list. Each consecutive triple is one triangle.</summary>
    public int[] Indices { get; private set; } = [];

    /// <summary>Draw sections: each covers a slice of <see cref="Indices"/> with one texture.</summary>
    public SskDrawSection[] Sections { get; private set; } = [];

    /// <summary>
    /// Which original <c>.trk</c> chunk (0-based, indexes the file's own chunk offset table)
    /// each triangle came from. Parallel to <see cref="Indices"/> at triangle granularity —
    /// <c>TriangleChunkIndex[i]</c> is the chunk for <c>Indices[i*3..i*3+2]</c>. Exists so a
    /// glTF export can reproduce the file's own exact chunk partitioning (see
    /// <c>GltfWriter</c>'s per-chunk mesh export) rather than only approximating it by nearest
    /// spatial anchor on reimport.
    /// </summary>
    public int[] TriangleChunkIndex { get; private set; } = [];

    /// <summary>
    /// Which <c>BEGIN_VTXS</c> block (display-list primitive batch) each triangle was decoded
    /// from — parallel to <see cref="TriangleChunkIndex"/>. Ids are unique across the whole
    /// track, so a change in value between consecutive triangles marks a block boundary.
    /// </summary>
    public int[] TriangleBlockId { get; private set; } = [];

    /// <summary>
    /// The <c>BEGIN_VTXS</c> primitive mode of the block each triangle came from (0 = separate
    /// triangles, 1 = separate quads, 2 = triangle strip, 3 = quad strip), parallel to
    /// <see cref="TriangleBlockId"/>.
    ///
    /// <para>
    /// This is preserved because it is genuinely <em>source</em> information that cannot be
    /// recovered from the triangles: real tracks routinely ship geometry as a mode-0 loose batch
    /// even where the triangles would chain into a perfectly valid strip (confirmed by direct
    /// comparison against the shipped files), so any re-stripping heuristic will disagree with
    /// the original for some chunks. Carrying the real block layout through a glTF round trip is
    /// what allows the rebuilt <c>.trk</c> to be byte-identical. See <c>GltfWriter</c>.
    /// </para>
    /// </summary>
    public int[] TriangleBlockMode { get; private set; } = [];

    /// <summary>
    /// Per <c>(chunk, textureSlot)</c> group, one flag per <em>emitted vertex</em> (in display-list
    /// emission order, concatenated across that group's blocks) recording whether the source file
    /// emitted a <c>TEXCOORD</c> opcode for it.
    ///
    /// <para>
    /// Needed because <c>TEXCOORD</c> emission is not fully predictable from the decoded values.
    /// Real files both re-emit a <c>TEXCOORD</c> identical to the one already in effect, and omit
    /// one where a value-caching rule would emit it — most visibly in untextured chunks, where
    /// some tracks emit a single <c>TEXCOORD 0</c> per block and others emit none at all, with
    /// identical decoded UVs either way. That difference lives in the original art/tool, not in
    /// the geometry, so it is carried through the glTF round trip rather than guessed at. The
    /// writer treats these as advisory: it will emit anyway whenever the value genuinely changed,
    /// so a stale flag can never produce wrong UVs. See <c>TrkWriter</c> and <c>GltfWriter</c>.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<(int Chunk, int Texture), bool[]> VertexTexcoordEmitted { get; private set; }
        = new Dictionary<(int, int), bool[]>();

    /// <summary>
    /// Which <c>TEXIMAGE_PARAM</c> group each triangle was decoded from — parallel to
    /// <see cref="TriangleBlockId"/>, unique across the track. A chunk can open more than one
    /// group for the *same* texture (Madagascar Kartz's PlaneWreck chunk 0 opens two untextured
    /// groups), and grouping purely by texture index would silently merge them, so the real
    /// boundaries are carried through the glTF alongside the block layout.
    /// </summary>
    public int[] TriangleGroupId { get; private set; } = [];

    /// <summary>The file's own <c>numTotal</c> — total chunk count, primary and detail combined.</summary>
    public int ChunkCount { get; private set; }

    /// <summary>Textures in slot order. <c>Sections[i].TextureIndex</c> indexes into this list.</summary>
    public SskTexture[] Textures { get; private set; } = [];

    /// <summary>
    /// One past the highest slot the geometry indexes, or 0 when nothing is textured. The
    /// runtime table is at least this long even when the track owns fewer textures than that,
    /// so a slot can legitimately hold nothing.
    /// </summary>
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

    /// <summary>How the geometry in one texture slot uses its texture.</summary>
    /// <param name="MaxU">Largest absolute U coordinate, in texels.</param>
    /// <param name="MaxV">Largest absolute V coordinate, in texels.</param>
    /// <param name="Samples">Triangle corners drawn with this slot; 0 means nothing draws it.</param>
    public readonly record struct SlotUsage(float MaxU, float MaxV, int Samples);

    /// <summary>
    /// Measures the UV extent of the geometry in each texture slot.
    /// </summary>
    /// <remarks>
    /// Comparing these extents against a candidate texture's dimensions is how
    /// <see cref="SskTextureSlotSolver"/> reconstructs the slot order, so this deliberately
    /// reads back the already-decoded geometry rather than walking the display lists again.
    /// </remarks>
    public SlotUsage[] MeasureSlotUsage(int slotCount)
    {
        if (slotCount <= 0)
        {
            return [];
        }

        float[] maxU = new float[slotCount];
        float[] maxV = new float[slotCount];
        int[] samples = new int[slotCount];

        foreach (SskDrawSection section in Sections)
        {
            int slot = section.TextureIndex;
            if (slot < 0 || slot >= slotCount)
            {
                continue;
            }

            int end = SMath.Min(section.IndexFirst + section.IndexCount, Indices.Length);
            for (int i = section.IndexFirst; i < end; i++)
            {
                int v = Indices[i];
                if (v < 0 || (v * 2) + 1 >= Uvs.Length)
                {
                    continue;
                }

                maxU[slot] = SMath.Max(maxU[slot], SMath.Abs(Uvs[v * 2]));
                maxV[slot] = SMath.Max(maxV[slot], SMath.Abs(Uvs[(v * 2) + 1]));
                samples[slot]++;
            }
        }

        SlotUsage[] usage = new SlotUsage[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            usage[i] = new SlotUsage(maxU[i], maxV[i], samples[i]);
        }

        return usage;
    }

    /// <summary>
    /// Replaces the slot textures on an already-parsed track.
    /// </summary>
    /// <remarks>
    /// The slot order cannot be known until the geometry has been measured, so callers parse
    /// once with no textures, solve, then attach — rather than decoding the TRK twice.
    /// </remarks>
    public void AttachTextures(IReadOnlyList<SskTexture> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        Textures = [.. textures];
    }

    /// <inheritdoc cref="AttachTextures(IReadOnlyList{SskTexture})"/>
    public void AttachTextures(IReadOnlyList<DwkEntry> texEntries)
    {
        ArgumentNullException.ThrowIfNull(texEntries);
        Textures = [.. DecodeTextures(texEntries)];
    }

    /// <summary>
    /// Replaces the slot textures from a table that may have gaps — slots the track indexes
    /// but no texture reaches. Those decode to a blank, exactly like an unreadable entry.
    /// </summary>
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

    /// <summary>Decodes archive entries to textures, substituting a blank for anything unreadable.</summary>
    private static List<SskTexture> DecodeTextures(IReadOnlyList<DwkEntry> texEntries)
    {
        List<SskTexture> textures = new(texEntries.Count);
        foreach (DwkEntry e in texEntries)
        {
            textures.Add(SskTexture.IsTexture(e.Span) ? SskTexture.ParseForTrack(e.Span, e.Name) : new SskTexture());
        }

        return textures;
    }

    // ── DS GPU command parameter counts ──────────────────────────────────────
    private static readonly Dictionary<byte, int> CmdParams = new()
    {
        [0x00] = 0,
        [0x10] = 1,
        [0x11] = 0,
        // Real DS GX hardware's MTX_LOAD_4x4 takes 16 params, but this format's own usage of
        // opcode 0x12 doesn't — confirmed by direct byte counting against several real chunks'
        // own declared dlSize (a genuine 16-param read would run 12-15 words past the chunk's
        // own boundary): it's always the very last opcode of a chunk's DL, always exactly 1
        // param, always value 1 (see TrkWriter.BuildChunk's remarks). Almost certainly not a
        // literal GX FIFO command at all despite reusing hardware's opcode number for it — more
        // likely a tool-specific end-of-chunk marker. The 16-param assumption was silently
        // harmless here regardless (see the type-level history), since it only ever "ate" a few
        // already-zero trailing NOP words with nothing real after them to desync.
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

    // ── Public entry points ───────────────────────────────────────────────────

    /// <summary>
    /// Parses a TRK blob, decoding textures from the provided <see cref="DwkEntry"/> list.
    /// </summary>
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<DwkEntry> texEntries)
    {
        return ParseInternal(blob, DecodeTextures(texEntries));
    }

    /// <summary>
    /// Parses a TRK blob with pre-decoded textures.
    /// </summary>
    public static SskTrack Parse(ReadOnlySpan<byte> blob, IReadOnlyList<SskTexture> textures)
    {
        return ParseInternal(blob, [.. textures]);
    }

    public static bool IsTrack(ReadOnlySpan<byte> blob)
    {
        // Heuristic: chunk offset table always starts at 0x38 (immediately after the 56-byte header)
        return blob.Length >= 56 && BinaryRead.U32(blob, 0x0C) == 0x38;
    }

    // ── Core decoder ─────────────────────────────────────────────────────────

    private static SskTrack ParseInternal(ReadOnlySpan<byte> blob, List<SskTexture> textures)
    {
        int numTotal = BinaryRead.U16(blob, 0x02);
        int chunkTableStart = (int)BinaryRead.U32(blob, 0x0C);

        // The chunk offset table is what everything else hangs off, so one that does not fit
        // inside the blob is not a track worth decoding part of. Decoding nothing leaves an
        // empty scene, which the node already treats as "nothing to draw".
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
        // Stable sort by chunk only — NOT also by texture. Decode order already visits each
        // chunk's own texture groups in their real DL order (chunks are decoded sequentially,
        // and within a chunk, groups are emitted in DL order); a stable sort keyed only on
        // chunk preserves that relative order exactly, so downstream Sections still come out
        // texture-contiguous (texture only changes where the chunk's own groups actually
        // change) without destroying which texture group came first *within* a given chunk.
        // An earlier version sorted by texture globally across the whole track instead — good
        // for merging same-texture triangles into fewer viewport draw calls, but it discards
        // each chunk's own internal group order, which a byte-exact glTF round-trip (see
        // GltfWriter's per-chunk mesh export) needs preserved exactly.
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

        // Widened: 24 + texCount * 12 overflows an int long before texCount stops looking like
        // a plausible header field, and a negative header size put the display-list slice
        // before the start of the blob.
        long headerSize = 24 + ((long)texCount * 12);
        long dlOff = chunkOff + headerSize;

        if (dlSize == 0 || dlOff + dlSize > blob.Length)
        {
            return;
        }

        // Build tex_map: DL-relative byte offset of the TEXIMAGE_PARAM parameter → 0-based texture slot
        Dictionary<int, int> texMap = [];
        for (int ti = 0; ti < (int)texCount; ti++)
        {
            int entBase = chunkOff + 24 + (ti * 12);
            int dlByteOff = (int)BinaryRead.U32(blob, entBase + 0); // teximg_dl_offset
            int idx1 = (int)BinaryRead.U32(blob, entBase + 8);       // 1-based
            texMap[dlByteOff] = idx1 - 1;                             // store 0-based
        }

        // Copy the display list into an array so the ReadW closure can capture it
        // (ReadOnlySpan<byte> is a ref struct and cannot be captured in closures).
        byte[] dlArr = blob.Slice((int)dlOff, (int)dlSize).ToArray();
        int dlWords = dlArr.Length / 4;

        // Mutable decoder state
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

        // Which BEGIN_VTXS block each triangle came from, and that block's primitive mode.
        // Recorded so a glTF export can preserve the file's own block layout: whether a run of
        // triangles shipped as a mode-2 strip or a mode-0 loose batch is *source* information,
        // not something recoverable from the triangles themselves (real mode-0 blocks routinely
        // contain triangles that would strip perfectly well). See GltfWriter's block extras.
        int curBlock = -1;
        int curGroup = -1;
        blockCounter++; // ensure a chunk's first block never reuses the previous chunk's id
        groupCounter++;

        uint ReadW()
        {
            return word < dlWords
            ? BinaryPrimitives.ReadUInt32LittleEndian(dlArr.AsSpan(word++ * 4))
            : 0u;
        }

        // Set when a TEXCOORD opcode is seen, consumed by the next vertex emission — TEXCOORD
        // always precedes the vertex opcode it applies to. See VertexTexcoordEmitted.
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

            // Shifted out one at a time rather than gathered into an array first: this is the
            // inner loop of the decode, and a four-byte array per packed word came to hundreds
            // of thousands of allocations for a single track.
            for (int packed = 0; packed < 4; packed++)
            {
                byte cmd = (byte)((pack >> (packed * 8)) & 0xFF);
                int paramByteOff = word * 4; // DL-relative offset of first param word
                int np = CmdParams.TryGetValue(cmd, out int n) ? n : 0;
                uint p0 = np >= 1 ? ReadW() : 0u;
                uint p1 = np >= 2 ? ReadW() : 0u;
                uint p2 = np >= 3 ? ReadW() : 0u;
                for (int pi = 3; pi < np; pi++) { _ = ReadW(); } // skip any beyond 3

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
                        // Opens a new group even when it selects the same texture as the previous
                        // one — real chunks do that (see TriangleGroupId).
                        curGroup = ++groupCounter;
                        if (texMap.TryGetValue(paramByteOff, out int slot)) { curTex = slot; }
                        break;

                    case CmdBeginVtxs:
                        mode = (int)(p0 & 0x3); strip.Clear(); curBlock = ++blockCounter; break;

                    case CmdEndVtxs: break;
                }

                // Assemble primitives after each vertex command
                if (cmd is CmdVtx16 or CmdVtx10 or CmdVtxXy or CmdVtxXz or CmdVtxYz or CmdVtxDiff)
                {
                    int n2 = strip.Count;
                    switch (mode)
                    {
                        case 0: // separate triangles
                            if (n2 % 3 == 0 && n2 >= 3)
                            {
                                Tri(strip[n2 - 3], strip[n2 - 2], strip[n2 - 1]);
                            }
                            break;
                        case 1: // separate quads
                            if (n2 % 4 == 0 && n2 >= 4)
                            {
                                Tri(strip[n2 - 4], strip[n2 - 3], strip[n2 - 2]);
                                Tri(strip[n2 - 4], strip[n2 - 2], strip[n2 - 1]);
                            }
                            break;
                        case 2: // triangle strip (alternating winding)
                            if (n2 >= 3)
                            {
                                if ((n2 - 1) % 2 == 0)
                                { Tri(strip[n2 - 3], strip[n2 - 2], strip[n2 - 1]); }
                                else
                                { Tri(strip[n2 - 2], strip[n2 - 3], strip[n2 - 1]); }
                            }
                            break;
                        case 3: // quad strip
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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
