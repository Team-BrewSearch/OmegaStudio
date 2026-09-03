using System.Numerics;
using SMath = System.Math;
namespace Kartz.Formats;

public static class TrkWriter
{
    private const int HeaderSize = 0x38;
    private const int ChunkHeaderSize = 24;
    private const int TexRefEntrySize = 12;
    private const float VtxRange = 7.5f;
    private const byte CmdMtxPush = 0x11;
    private const byte CmdMtxPop = 0x12;
    private const byte CmdMtxScale = 0x1B;
    private const byte CmdMtxTrans = 0x1C;
    private const byte CmdColor = 0x20;
    private const byte CmdTexcoord = 0x22;
    private const byte CmdVtx16 = 0x23;
    private const byte CmdVtxDiff = 0x28;
    private const byte CmdPlttBase = 0x2B;
    private const byte CmdTeximageParam = 0x2A;
    private const byte CmdBeginVtxs = 0x40;
    private const byte CmdEndVtxs = 0x41;
    private const uint BaselineColor555 = 0x7FFF;
    public static byte[] Build(ReadOnlySpan<byte> originalTemplate, IReadOnlyList<TrkChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));
        }
        if (originalTemplate.Length < HeaderSize)
        {
            throw new ArgumentException("Template is too small to hold a .trk header.", nameof(originalTemplate));
        }
        int chunkCount = chunks.Count;
        byte[][] chunkBlobs = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            chunkBlobs[i] = chunks[i].Triangles.Count == 0
                    && TryCopyTemplateChunk(originalTemplate, i, out byte[]? copied)
                ? copied
                : BuildChunk(chunks[i], i);
        }
        byte[] header = originalTemplate[..HeaderSize].ToArray();
        WriteU16(header, 0x02, (ushort)chunkCount);
        WriteU32(header, 0x0C, HeaderSize);
        int[] tailFieldOffsets = [0x14, 0x1C, 0x24, 0x2C, 0x34];
        uint originalTailStart = ReadU32(header, 0x14);
        byte[] tail = originalTailStart > 0 && originalTailStart < originalTemplate.Length
            ? originalTemplate[(int)originalTailStart..].ToArray()
            : [];
        int offsetTableSize = chunkCount * 4;
        int positionTableSize = chunkCount * 12;
        int bodyStart = HeaderSize + offsetTableSize + positionTableSize;
        int[] chunkOffsets = new int[chunkCount];
        int cursor = bodyStart;
        for (int i = 0; i < chunkCount; i++)
        {
            chunkOffsets[i] = cursor;
            cursor += chunkBlobs[i].Length;
        }
        int newTailStart = cursor;
        if (tail.Length > 0)
        {
            long delta = newTailStart - originalTailStart;
            RelocateOffsetTable(tail, originalTemplate, originalTailStart, 0x34, 0x30, delta);
            foreach (int fieldOff in tailFieldOffsets)
            {
                uint orig = ReadU32(header, fieldOff);
                if (orig != 0)
                {
                    WriteU32(header, fieldOff, (uint)(orig + delta));
                }
            }
        }
        byte[] result = new byte[newTailStart + tail.Length];
        header.CopyTo(result, 0);
        for (int i = 0; i < chunkCount; i++)
        {
            WriteU32(result, HeaderSize + (i * 4), (uint)chunkOffsets[i]);
            int posOff = HeaderSize + offsetTableSize + (i * 12);
            Vector3 p = chunks[i].Position;
            WriteI32(result, posOff, (int)MathF.Round(p.X * 4096f));
            WriteI32(result, posOff + 4, (int)MathF.Round(p.Y * 4096f));
            WriteI32(result, posOff + 8, (int)MathF.Round(p.Z * 4096f));
        }
        for (int i = 0; i < chunkCount; i++)
        {
            chunkBlobs[i].CopyTo(result, chunkOffsets[i]);
        }
        tail.CopyTo(result, newTailStart);
        return result;
    }
    private static uint ReadU32(ReadOnlySpan<byte> buf, int off)
    {
        return (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));
    }
    public static Vector3[] ReadChunkPositions(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }
        int numTotal = template[0x02] | (template[0x03] << 8);
        int posTableStart = HeaderSize + (numTotal * 4);
        if (numTotal <= 0 || posTableStart + (numTotal * 12) > template.Length)
        {
            return [];
        }
        Vector3[] positions = new Vector3[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int off = posTableStart + (i * 12);
            int x = (int)ReadU32(template, off);
            int y = (int)ReadU32(template, off + 4);
            int z = (int)ReadU32(template, off + 8);
            positions[i] = new Vector3(x / 4096f, y / 4096f, z / 4096f);
        }
        return positions;
    }
    public static ushort[] ReadChunkTypes(ReadOnlySpan<byte> template, IReadOnlyList<int> chunkOffsets)
    {
        ArgumentNullException.ThrowIfNull(chunkOffsets);
        ushort[] types = new ushort[chunkOffsets.Count];
        for (int i = 0; i < chunkOffsets.Count; i++)
        {
            int chunkOff = chunkOffsets[i];
            types[i] = chunkOff + 4 <= template.Length
                ? (ushort)(template[chunkOff + 2] | (template[chunkOff + 3] << 8))
                : (ushort)0x000E;
        }
        return types;
    }
    public static (uint CoordScale, uint PackedParams)[] ReadChunkExtraFields(
        ReadOnlySpan<byte> template, IReadOnlyList<int> chunkOffsets)
    {
        ArgumentNullException.ThrowIfNull(chunkOffsets);
        (uint, uint)[] fields = new (uint, uint)[chunkOffsets.Count];
        for (int i = 0; i < chunkOffsets.Count; i++)
        {
            int chunkOff = chunkOffsets[i];
            fields[i] = chunkOff + 0x10 <= template.Length
                ? (ReadU32(template, chunkOff + 0x08), ReadU32(template, chunkOff + 0x0C))
                : (0u, 0u);
        }
        return fields;
    }
    public static float?[] ReadChunkVtxScales(ReadOnlySpan<byte> template, IReadOnlyList<int> chunkOffsets)
    {
        ArgumentNullException.ThrowIfNull(chunkOffsets);
        float?[] scales = new float?[chunkOffsets.Count];
        for (int i = 0; i < chunkOffsets.Count; i++)
        {
            int chunkOff = chunkOffsets[i];
            if (chunkOff + 24 > template.Length)
            {
                continue;
            }
            uint texCount = ReadU32(template, chunkOff + 0x10);
            uint dlSize = ReadU32(template, chunkOff + 0x04);
            int dlOff = chunkOff + 24 + ((int)texCount * 12);
            if (dlSize < 28 || dlOff + 28 > template.Length)
            {
                continue;
            }
            if (template[dlOff] != 0x11 || template[dlOff + 1] != 0x1C || template[dlOff + 2] != 0x1B)
            {
                continue;
            }
            int scaleRaw = (int)ReadU32(template, dlOff + 16);
            scales[i] = scaleRaw / 4096f;
        }
        return scales;
    }
    public static ushort[] ReadChunkTypes(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }
        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }
        ushort[] types = new ushort[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            types[i] = chunkOff + 4 <= template.Length
                ? (ushort)(template[chunkOff + 2] | (template[chunkOff + 3] << 8))
                : (ushort)0x000E;
        }
        return types;
    }
    public static (uint CoordScale, uint PackedParams)[] ReadChunkExtraFields(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }
        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }
        (uint, uint)[] fields = new (uint, uint)[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            fields[i] = chunkOff + 0x10 <= template.Length
                ? (ReadU32(template, chunkOff + 0x08), ReadU32(template, chunkOff + 0x0C))
                : (0u, 0u);
        }
        return fields;
    }
    public static float?[] ReadChunkVtxScales(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }
        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }
        float?[] scales = new float?[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            if (chunkOff + 24 > template.Length)
            {
                continue;
            }
            uint texCount = ReadU32(template, chunkOff + 0x10);
            uint dlSize = ReadU32(template, chunkOff + 0x04);
            int dlOff = chunkOff + 24 + ((int)texCount * 12);
            if (dlSize < 28 || dlOff + 28 > template.Length)
            {
                continue;
            }
            if (template[dlOff] != 0x11 || template[dlOff + 1] != 0x1C || template[dlOff + 2] != 0x1B)
            {
                continue;
            }
            int scaleRaw = (int)ReadU32(template, dlOff + 16);
            scales[i] = scaleRaw / 4096f;
        }
        return scales;
    }
    private static bool TryCopyTemplateChunk(ReadOnlySpan<byte> template, int chunkIndex, out byte[] copy)
    {
        copy = [];
        int tableEntry = HeaderSize + (chunkIndex * 4);
        if (tableEntry + 4 > template.Length)
        {
            return false;
        }
        uint numTotal = (uint)(template[0x02] | (template[0x03] << 8));
        if (chunkIndex >= numTotal)
        {
            return false;
        }
        uint off = ReadU32(template, tableEntry);
        if (off == 0 || off + ChunkHeaderSize > template.Length)
        {
            return false;
        }
        uint texCount = ReadU32(template, (int)off + 0x10);
        uint dlSize = ReadU32(template, (int)off + 0x04);
        long total = ChunkHeaderSize + ((long)texCount * TexRefEntrySize) + dlSize;
        if (texCount != 0 || off + total > template.Length)
        {
            return false;
        }
        copy = template.Slice((int)off, (int)total).ToArray();
        return true;
    }
    private static void RelocateOffsetTable(
        byte[] tail, ReadOnlySpan<byte> originalTemplate, uint originalTailStart,
        int offsetField, int countField, long delta)
    {
        if (delta == 0 || tail.Length == 0)
        {
            return;
        }
        uint sectionStart = ReadU32(originalTemplate, offsetField);
        uint count = ReadU32(originalTemplate, countField);
        if (sectionStart == 0 || count == 0 || count > 4096)
        {
            return;
        }
        long indexInTail = (long)sectionStart - originalTailStart;
        long tableBytes = (long)count * 4;
        if (indexInTail < 0 || indexInTail + tableBytes > tail.Length)
        {
            return;
        }
        uint originalFileLength = (uint)(originalTailStart + tail.Length);
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint value = ReadU32(tail, (int)indexInTail + (i * 4));
            bool inFile = value >= sectionStart && value < originalFileLength;
            bool ascending = i == 0 || value > previous;
            if (!inFile || !ascending)
            {
                return;
            }
            previous = value;
        }
        if (ReadU32(tail, (int)indexInTail) != sectionStart + tableBytes)
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            int at = (int)indexInTail + (i * 4);
            WriteU32(tail, at, (uint)(ReadU32(tail, at) + delta));
        }
    }
    public static byte[] BuildChunk(TrkChunk chunk, int chunkIndex)
    {
        if (chunk.Triangles.Count == 0)
        {
            byte[] empty = new byte[ChunkHeaderSize];
            WriteU16(empty, 0x00, (ushort)chunkIndex);
            WriteU16(empty, 0x02, chunk.ChunkType);
            WriteU32(empty, 0x04, 0);
            WriteU32(empty, 0x08, chunk.CoordScale);
            WriteU32(empty, 0x0C, chunk.PackedParams);
            WriteU32(empty, 0x10, 0);
            WriteU32(empty, 0x14, 0);
            return empty;
        }
        (Vector3 translate, float scale) = FitTransform(chunk);
        List<IGrouping<int, (TrkImportPrimitive Prim, int A, int B, int C)>> groups = chunk.Triangles
            .GroupBy(t => t.Prim.TextureIndex)
            .ToList();
        List<Op> ops = [
            new Op(CmdMtxPush),
            new Op(
                CmdMtxTrans,
                EncodeFixed20_12(translate.X),
                EncodeFixed20_12(translate.Y),
                EncodeFixed20_12(translate.Z),
                3),
            new Op(CmdMtxScale, EncodeFixed20_12(scale), EncodeFixed20_12(scale), EncodeFixed20_12(scale), 3)];
        List<int> texImageOpIndex = [];
        List<int> plttBaseOpIndex = [];
        List<int> textureSlot1Based = [];
        uint lastTexcoord = 0;
        bool hasTexcoord = false;
        Dictionary<TrkImportPrimitive, int> vertexOrdinal = [];
        foreach (IGrouping<int, (TrkImportPrimitive Prim, int A, int B, int C)> group in groups)
        {
            bool untextured = group.Key < 0;
            void EmitGroupHeader()
            {
                if (!untextured)
                {
                    texImageOpIndex.Add(ops.Count);
                }
                ops.Add(new Op(CmdTeximageParam, untextured ? 0x00000000u : 0x40000000u, 0, 0, 1));
                if (!untextured)
                {
                    textureSlot1Based.Add(group.Key + 1);
                    plttBaseOpIndex.Add(ops.Count);
                    ops.Add(new Op(CmdPlttBase, 0, 0, 0, 1));
                }
                ops.Add(new Op(CmdColor, BaselineColor555, 0, 0, 1));
            }
            EmitGroupHeader();
            uint lastColor555 = BaselineColor555;
            List<(int Mode, List<(TrkImportPrimitive Prim, int V)> Verts, bool StartsGroup)> blocks = [];
            List<(TrkImportPrimitive Prim, int A, int B, int C)> singles = [];
            foreach (IGrouping<TrkImportPrimitive, (TrkImportPrimitive Prim, int A, int B, int C)> byPrim in group.GroupBy(t => t.Prim))
            {
                TrkImportPrimitive prim = byPrim.Key;
                List<(int A, int B, int C)> tris = [.. byPrim.Select(t => (t.A, t.B, t.C))];
                List<(int Mode, List<int> Verts)>? recorded = SplitByBlocks(tris, prim.Blocks);
                if (recorded is not null)
                {
                    IReadOnlyList<int> breaks = prim.GroupBreaks ?? [];
                    for (int bi = 0; bi < recorded.Count; bi++)
                    {
                        (int mode, List<int> verts) = recorded[bi];
                        blocks.Add((mode, [.. verts.Select(v => (prim, v))], breaks.Contains(bi)));
                    }
                    continue;
                }
                foreach ((int mode, List<int> verts) in BuildStrips(tris))
                {
                    if (mode == 2)
                    {
                        blocks.Add((2, [.. verts.Select(v => (prim, v))], false));
                        continue;
                    }
                    for (int v = 0; v + 2 < verts.Count; v += 3)
                    {
                        singles.Add((prim, verts[v], verts[v + 1], verts[v + 2]));
                    }
                }
            }
            if (singles.Count > 0)
            {
                List<(TrkImportPrimitive, int)> flat = [];
                foreach ((TrkImportPrimitive sPrim, int a, int b, int c) in singles)
                {
                    flat.Add((sPrim, a)); flat.Add((sPrim, b)); flat.Add((sPrim, c));
                }
                blocks.Add((0, flat, false));
            }
            foreach ((int mode, List<(TrkImportPrimitive Prim, int V)> verts, bool startsGroup) in blocks)
            {
                if (startsGroup)
                {
                    EmitGroupHeader();
                    lastColor555 = BaselineColor555;
                }
                ops.Add(new Op(CmdBeginVtxs, (uint)mode, 0, 0, 1));
                int stripPos = 0;
                (int X, int Y, int Z) lastQuant = default;
                foreach ((TrkImportPrimitive vPrim, int v) in verts)
                {
                    _ = vertexOrdinal.TryGetValue(vPrim, out int ordinal);
                    vertexOrdinal[vPrim] = ordinal + 1;
                    bool forceTexcoord = vPrim.TexcoordEmit is { } flags && ordinal < flags.Count
                        ? flags[ordinal]
                        : stripPos == 0 && hasTexcoord;
                    EmitVertex(
                        ops, vPrim, v, translate, scale, ref lastColor555, ref lastTexcoord,
                        ref hasTexcoord, ref stripPos, ref lastQuant, forceTexcoord);
                }
                EmitEndVtxs(ops);
            }
        }
        ops.Add(new Op(CmdMtxPop, 1, 0, 0, 1));
        byte[] dl = SerializeOps(ops, out Dictionary<int, int> byteOffsets);
        int texCount = texImageOpIndex.Count;
        byte[] result = new byte[ChunkHeaderSize + (texCount * TexRefEntrySize) + dl.Length];
        WriteU16(result, 0x00, (ushort)chunkIndex);
        WriteU16(result, 0x02, chunk.ChunkType);
        WriteU32(result, 0x04, (uint)dl.Length);
        WriteU32(result, 0x08, EncodeFixed20_12(scale));
        WriteU32(result, 0x0C, chunk.PackedParams);
        WriteU32(result, 0x10, (uint)texCount);
        WriteU32(result, 0x14, 0);
        for (int g = 0; g < texCount; g++)
        {
            int entOff = ChunkHeaderSize + (g * TexRefEntrySize);
            WriteU32(result, entOff, (uint)byteOffsets[texImageOpIndex[g]]);
            WriteU32(result, entOff + 4, (uint)byteOffsets[plttBaseOpIndex[g]]);
            WriteU32(result, entOff + 8, (uint)textureSlot1Based[g]);
        }
        dl.CopyTo(result, ChunkHeaderSize + (texCount * TexRefEntrySize));
        return result;
    }
    private static (Vector3 Translate, float Scale) FitTransform(TrkChunk chunk)
    {
        Vector3 translate = chunk.Position;
        if (chunk.Triangles.Count == 0)
        {
            return (translate, chunk.TemplateScale ?? 1f);
        }
        float maxAbs = 0f;
        foreach ((TrkImportPrimitive prim, int a, int b, int c) in chunk.Triangles)
        {
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, a, translate));
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, b, translate));
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, c, translate));
        }
        if (chunk.TemplateScale is { } templateScale && maxAbs <= templateScale * VtxRange)
        {
            return (translate, templateScale);
        }
        float scale = 1f;
        while (maxAbs > scale * VtxRange)
        {
            scale *= 2f;
        }
        return (translate, scale);
    }
    private static float LocalExtent(TrkImportPrimitive prim, int vertex, Vector3 translate)
    {
        Vector3 local = new Vector3(
            prim.Positions[vertex * 3],
            prim.Positions[(vertex * 3) + 1],
            prim.Positions[(vertex * 3) + 2]) - translate;
        return MathF.Max(MathF.Abs(local.X), MathF.Max(MathF.Abs(local.Y), MathF.Abs(local.Z)));
    }
    private static List<(int Mode, List<int> Verts)> BuildStrips(
        List<(int A, int B, int C)> triangles)
    {
        List<(int Mode, List<int> Verts)> runs = [];
        int i = 0;
        while (i < triangles.Count)
        {
            (int a, int b, int c) = triangles[i];
            List<int> strip = [a, b, c];
            int consumed = 1;
            while (i + consumed < triangles.Count)
            {
                int next = strip.Count;
                int last = strip[next - 1];
                int prev = strip[next - 2];
                (int x, int y) = next % 2 == 0 ? (prev, last) : (last, prev);
                (int ta, int tb, int tc) = triangles[i + consumed];
                if (ta != x || tb != y)
                {
                    break;
                }
                strip.Add(tc);
                consumed++;
            }
            if (consumed >= 2)
            {
                runs.Add((2, strip));
                i += consumed;
                continue;
            }
            List<int> tail = new((triangles.Count - i) * 3);
            for (int t = i; t < triangles.Count; t++)
            {
                (int ta, int tb, int tc) = triangles[t];
                tail.Add(ta);
                tail.Add(tb);
                tail.Add(tc);
            }
            runs.Add((0, tail));
            break;
        }
        return runs;
    }
    private static List<(int Mode, List<int> Verts)>? SplitByBlocks(
        List<(int A, int B, int C)> triangles, IReadOnlyList<(int Mode, int TriangleCount)>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return null;
        }
        if (blocks.Sum(b => b.TriangleCount) != triangles.Count)
        {
            return null;
        }
        List<(int, List<int>)> result = [];
        int at = 0;
        foreach ((int mode, int count) in blocks)
        {
            if (mode == 0)
            {
                List<int> verts = new(count * 3);
                for (int i = 0; i < count; i++)
                {
                    (int a, int b, int c) = triangles[at + i];
                    verts.Add(a); verts.Add(b); verts.Add(c);
                }
                result.Add((0, verts));
            }
            else if (mode == 2)
            {
                (int sa, int sb, int sc) = triangles[at];
                List<int> strip = [sa, sb, sc];
                for (int i = 1; i < count; i++)
                {
                    int next = strip.Count;
                    int last = strip[next - 1], prev = strip[next - 2];
                    (int x, int y) = next % 2 == 0 ? (prev, last) : (last, prev);
                    int? third = MatchThirdVertex(triangles[at + i], x, y);
                    if (third is null)
                    {
                        return null;
                    }
                    strip.Add(third.Value);
                }
                result.Add((2, strip));
            }
            else
            {
                return null;
            }
            at += count;
        }
        return result;
    }
    private static int? MatchThirdVertex((int A, int B, int C) tri, int x, int y)
    {
        return tri.A == x && tri.B == y ? tri.C : tri.B == x && tri.C == y ? tri.A : tri.C == x && tri.A == y ? tri.B : null;
    }
    private static void EmitEndVtxs(List<Op> ops)
    {
        ops.Add(new Op(CmdEndVtxs));
        if (ops.Count % 4 == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                ops.Add(new Op(0x00));
            }
        }
    }
    private static void EmitVertex(
        List<Op> ops, TrkImportPrimitive prim, int v, Vector3 translate, float scale,
        ref uint lastColor555, ref uint lastTexcoord, ref bool hasTexcoord,
        ref int stripPos, ref (int X, int Y, int Z) lastQuant, bool forceTexcoord)
    {
        float px = prim.Positions[v * 3], py = prim.Positions[(v * 3) + 1], pz = prim.Positions[(v * 3) + 2];
        float u = prim.Uvs[v * 2], t = prim.Uvs[(v * 2) + 1];
        byte cr = prim.Colors[v * 4], cg = prim.Colors[(v * 4) + 1], cb = prim.Colors[(v * 4) + 2];
        uint color555 = EncodeColor555(cr, cg, cb);
        if (color555 != lastColor555)
        {
            ops.Add(new Op(CmdColor, color555, 0, 0, 1));
            lastColor555 = color555;
        }
        uint texcoord = EncodeTexcoord(u, t);
        if (forceTexcoord || texcoord != lastTexcoord)
        {
            ops.Add(new Op(CmdTexcoord, texcoord, 0, 0, 1));
            lastTexcoord = texcoord;
            hasTexcoord = true;
        }
        float lx = (px - translate.X) / scale, ly = (py - translate.Y) / scale, lz = (pz - translate.Z) / scale;
        int qx = EncodeFixed4_12(lx), qy = EncodeFixed4_12(ly), qz = EncodeFixed4_12(lz);
        if (stripPos > 0 && qx == lastQuant.X && qy == lastQuant.Y && qz == lastQuant.Z)
        {
            ops.Add(new Op(CmdVtxDiff, 0, 0, 0, 1));
        }
        else
        {
            uint p0 = (ushort)qx | ((uint)(ushort)qy << 16);
            uint p1 = (ushort)qz;
            ops.Add(new Op(CmdVtx16, p0, p1, 0, 2));
        }
        lastQuant = (qx, qy, qz);
        stripPos++;
    }
    private static int EncodeFixed4_12(float v)
    {
        return SMath.Clamp((int)MathF.Round(v * 4096f), short.MinValue, short.MaxValue);
    }
    private static uint EncodeFixed20_12(float v)
    {
        return unchecked((uint)(int)MathF.Round(v * 4096f));
    }
    private static uint EncodeTexcoord(float u, float v)
    {
        short us = (short)SMath.Clamp((int)MathF.Round(u * 16f), short.MinValue, short.MaxValue);
        short vs = (short)SMath.Clamp((int)MathF.Round(v * 16f), short.MinValue, short.MaxValue);
        return (ushort)us | ((uint)(ushort)vs << 16);
    }
    private static uint EncodeColor555(byte r, byte g, byte b)
    {
        uint c = (uint)(r >> 3) | ((uint)(g >> 3) << 5) | ((uint)(b >> 3) << 10);
        return c & 0x7FFF;
    }
    private readonly record struct Op(byte Cmd, uint P0 = 0, uint P1 = 0, uint P2 = 0, int ParamCount = 0);
    private static byte[] SerializeOps(List<Op> ops, out Dictionary<int, int> paramByteOffsets)
    {
        paramByteOffsets = [];
        List<byte> bytes = [];
        int words = 0;
        void AppendWord(uint w)
        {
            bytes.Add((byte)(w & 0xFF));
            bytes.Add((byte)((w >> 8) & 0xFF));
            bytes.Add((byte)((w >> 16) & 0xFF));
            bytes.Add((byte)((w >> 24) & 0xFF));
        }
        for (int g = 0; g < ops.Count; g += 4)
        {
            int groupSize = SMath.Min(4, ops.Count - g);
            byte b0 = ops[g].Cmd;
            byte b1 = groupSize > 1 ? ops[g + 1].Cmd : (byte)0;
            byte b2 = groupSize > 2 ? ops[g + 2].Cmd : (byte)0;
            byte b3 = groupSize > 3 ? ops[g + 3].Cmd : (byte)0;
            AppendWord((uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)));
            words++;
            for (int k = 0; k < groupSize; k++)
            {
                Op op = ops[g + k];
                if (op.Cmd is CmdTeximageParam or CmdPlttBase)
                {
                    paramByteOffsets[g + k] = words * 4;
                }
                if (op.ParamCount >= 1)
                {
                    AppendWord(op.P0);
                    words++;
                }
                if (op.ParamCount >= 2)
                {
                    AppendWord(op.P1);
                    words++;
                }
                if (op.ParamCount >= 3)
                {
                    AppendWord(op.P2);
                    words++;
                }
            }
        }
        return [.. bytes];
    }
    private static void WriteU16(byte[] buf, int off, ushort v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)(v >> 8);
    }
    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)(v >> 24);
    }
    private static void WriteI32(byte[] buf, int off, int v)
    {
        WriteU32(buf, off, unchecked((uint)v));
    }
}
