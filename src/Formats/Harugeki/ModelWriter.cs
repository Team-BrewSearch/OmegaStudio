using System.Buffers.Binary;
using Omega.Plugins;
namespace Harugeki.Formats;
public static class ModelWriter
{
    private const int TriangleCountTable = 0x30;
    private const int MaterialTable = 0x70;
    private const int CountBlockGap = 4;
    private const int CountBlockSize = 0x10;
    private const int PreambleSize = 0xCC;
    private const int SkinVertexStride = 24;
    private const int MaxMaterials = 16;
    private const int TextureTable = 0x10;
    private const int NodeTexturePointer = 0x24;
    public static byte[] ReplaceGeometry(
        ReadOnlySpan<byte> template, IReadOnlyDictionary<int, ModelGeometry> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        Layout layout = Layout.Read(template);
        foreach (int node in replacements.Keys)
        {
            if (!layout.MeshNodes.Contains(node))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(replacements),
                    $"node {node} is not a mesh node; the mesh nodes are " +
                    $"[{string.Join(", ", layout.MeshNodes)}]");
            }
        }
        byte[][] nodes = new byte[layout.NodeOffsets.Length][];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = BuildNode(template, layout, i, replacements);
        }
        return Assemble(template, layout, nodes);
    }
    public static byte[] Rebuild(ReadOnlySpan<byte> template)
    {
        return ReplaceGeometry(template, new Dictionary<int, ModelGeometry>());
    }
    private static byte[] BuildNode(
        ReadOnlySpan<byte> blob, Layout layout, int node,
        IReadOnlyDictionary<int, ModelGeometry> replacements)
    {
        int offset = layout.NodeOffsets[node];
        if (offset <= 0)
        {
            return [];
        }
        ReadOnlySpan<byte> original = blob[offset..layout.NodeEnds[node]];
        if (layout.MeshNodes.Contains(node))
        {
            return replacements.TryGetValue(node, out ModelGeometry? geometry)
                ? BuildMesh(original, node, geometry)
                : original.ToArray();
        }
        int bone = layout.BoneOrdinals.IndexOf(node);
        if (bone < 0 || original.Length < RingBone.WeightsOffset)
        {
            return original.ToArray();
        }
        int boundTo = (int)BinaryRead.U32(original, 0x80);
        return !replacements.TryGetValue(boundTo, out ModelGeometry? owner)
            ? original.ToArray()
            : BuildBone(
            original,
            owner.BoneWeights.TryGetValue(bone, out (int[] Vertices, float[] Weights) w) ? w : ([], []));
    }
    private static byte[] BuildBone(ReadOnlySpan<byte> original, (int[] Vertices, float[] Weights) weights)
    {
        if (original.Length < RingBone.WeightsOffset)
        {
            return original.ToArray();
        }
        byte[] result = new byte[RingBone.WeightsOffset + (weights.Vertices.Length * 8)];
        original[..RingBone.WeightsOffset].CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x84), (uint)weights.Vertices.Length);
        for (int i = 0; i < weights.Vertices.Length; i++)
        {
            int at = RingBone.WeightsOffset + (i * 8);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), (uint)weights.Vertices[i]);
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at + 4), weights.Weights[i]);
        }
        return result;
    }
    private static byte[] BuildMesh(ReadOnlySpan<byte> original, int nodeIndex, ModelGeometry geometry)
    {
        geometry.Validate("Imported mesh");
        int materials = geometry.MaterialIndices.Count;
        if (materials > MaxMaterials)
        {
            throw new InvalidDataException(
                $"Imported mesh has {materials} materials; the format's triangle count table holds {MaxMaterials}.");
        }
        int templateMaterials = CountMaterials(original);
        int oldCounts = MaterialTable + (0x44 * templateMaterials) + CountBlockGap;
        if (oldCounts + CountBlockSize + PreambleSize > original.Length)
        {
            throw new InvalidDataException("Mesh node is truncated; it cannot be used as a replacement target.");
        }
        int vertices = geometry.VertexCount;
        int triangles = geometry.TriangleCount;
        int header = MaterialTable + (0x44 * materials) + CountBlockGap + CountBlockSize + PreambleSize;
        int body = (vertices * SkinVertexStride) + (triangles * ((3 * RingModel.VertexStride) + RingModel.FaceTrailer));
        byte[] result = new byte[header + body];
        Span<byte> span = result;
        original[..TriangleCountTable].CopyTo(span);
        for (int i = 0; i < MaxMaterials; i++)
        {
            uint count = i < materials ? (uint)(geometry.MaterialIndices[i].Length / 3) : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span[(TriangleCountTable + (i * 4))..], count);
        }
        for (int i = 0; i < materials; i++)
        {
            int source = MaterialTable + (0x44 * System.Math.Min(i, templateMaterials - 1));
            original.Slice(source, 0x44).CopyTo(span[(MaterialTable + (0x44 * i))..]);
        }
        original.Slice(oldCounts - CountBlockGap, CountBlockGap)
            .CopyTo(span[(MaterialTable + (0x44 * materials))..]);
        int counts = MaterialTable + (0x44 * materials) + CountBlockGap;
        BinaryPrimitives.WriteUInt32LittleEndian(span[counts..], (uint)vertices);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(counts + 4)..], (uint)vertices);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(counts + 8)..], (uint)triangles);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(counts + 12)..], (uint)nodeIndex);
        original.Slice(oldCounts + CountBlockSize, PreambleSize)
            .CopyTo(span[(counts + CountBlockSize)..]);
        WriteVertexArrays(span, counts + CountBlockSize + PreambleSize, geometry);
        return result;
    }
    private static void WriteVertexArrays(Span<byte> span, int at, ModelGeometry geometry)
    {
        int vertices = geometry.VertexCount;
        for (int i = 0; i < vertices; i++)
        {
            WriteMirrored(span[(at + (i * 12))..], geometry.Positions, i);
            WriteMirrored(span[(at + (vertices * 12) + (i * 12))..], geometry.Normals, i);
        }
        int draw = at + (vertices * SkinVertexStride);
        int faceStride = (3 * RingModel.VertexStride) + RingModel.FaceTrailer;
        int face = 0;
        foreach (int[] group in geometry.MaterialIndices)
        {
            for (int t = 0; t < group.Length; t += 3, face++)
            {
                int start = draw + (face * faceStride);
                float[][] corner = new float[3][];
                for (int k = 0; k < 3; k++)
                {
                    int vertex = group[t + k];
                    int v = start + (k * RingModel.VertexStride);
                    WriteMirrored(span[v..], geometry.Positions, vertex);
                    WriteMirrored(span[(v + 0x0C)..], geometry.Normals, vertex);
                    BinaryPrimitives.WriteSingleLittleEndian(span[(v + 0x18)..], geometry.Uvs[vertex * 2]);
                    BinaryPrimitives.WriteSingleLittleEndian(span[(v + 0x1C)..], geometry.Uvs[(vertex * 2) + 1]);
                    span[v + 0x20] = geometry.Colors[(vertex * 4) + 2];
                    span[v + 0x21] = geometry.Colors[(vertex * 4) + 1];
                    span[v + 0x22] = geometry.Colors[vertex * 4];
                    span[v + 0x23] = geometry.Colors[(vertex * 4) + 3];
                    BinaryPrimitives.WriteUInt16LittleEndian(span[(v + 0x24)..], (ushort)vertex);
                    BinaryPrimitives.WriteUInt16LittleEndian(span[(v + 0x26)..], (ushort)vertex);
                    corner[k] =
                    [
                        -geometry.Positions[vertex * 3],
                        geometry.Positions[(vertex * 3) + 1],
                        -geometry.Positions[(vertex * 3) + 2],
                    ];
                }
                WriteFaceNormal(span[(start + (3 * RingModel.VertexStride))..], corner);
            }
        }
    }
    private static void WriteMirrored(Span<byte> destination, float[] source, int vertex)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, -source[vertex * 3]);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], source[(vertex * 3) + 1]);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], -source[(vertex * 3) + 2]);
    }
    private static void WriteFaceNormal(Span<byte> destination, float[][] corner)
    {
        float ax = corner[1][0] - corner[0][0], ay = corner[1][1] - corner[0][1], az = corner[1][2] - corner[0][2];
        float bx = corner[2][0] - corner[0][0], by = corner[2][1] - corner[0][1], bz = corner[2][2] - corner[0][2];
        float nx = (ay * bz) - (az * by);
        float ny = (az * bx) - (ax * bz);
        float nz = (ax * by) - (ay * bx);
        float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (length > 1e-12f)
        {
            nx /= length;
            ny /= length;
            nz /= length;
        }
        BinaryPrimitives.WriteSingleLittleEndian(destination, nx);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], ny);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], nz);
        destination[12..RingModel.FaceTrailer].Clear();
    }
    private static int CountMaterials(ReadOnlySpan<byte> mesh)
    {
        int materials = 0;
        while (materials < MaxMaterials &&
               BinaryRead.U32(mesh, TriangleCountTable + (materials * 4)) != 0)
        {
            materials++;
        }
        return System.Math.Max(1, materials);
    }
    private static byte[] Assemble(ReadOnlySpan<byte> template, Layout layout, byte[][] nodes)
    {
        int nodeCount = layout.NodeOffsets.Length;
        int first = RingModel.NodeTableOffset + (nodeCount * 4);
        int[] placed = new int[nodeCount];
        int at = first;
        for (int i = 0; i < nodeCount; i++)
        {
            if (layout.NodeOffsets[i] <= 0)
            {
                placed[i] = 0;
                continue;
            }
            placed[i] = at;
            at += nodes[i].Length;
        }
        Dictionary<int, int> textureMoves = [];
        int textureAt = at;
        foreach (int texture in layout.TextureOffsets.Where(o => o > 0).Distinct().Order())
        {
            textureMoves[texture] = textureAt;
            textureAt += layout.TextureSizes[texture];
        }
        byte[] result = new byte[textureAt];
        template[..TextureTable].CopyTo(result);
        for (int i = 0; i < layout.TextureOffsets.Length; i++)
        {
            uint moved = layout.TextureOffsets[i] > 0
                ? (uint)textureMoves[layout.TextureOffsets[i]]
                : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(TextureTable + (i * 4)), moved);
        }
        for (int i = 0; i < nodeCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(RingModel.NodeTableOffset + (i * 4)), (uint)placed[i]);
            if (placed[i] > 0)
            {
                nodes[i].CopyTo(result.AsSpan(placed[i]));
            }
        }
        foreach ((int was, int now) in textureMoves)
        {
            template.Slice(was, layout.TextureSizes[was]).CopyTo(result.AsSpan(now));
        }
        Repoint(result, placed, textureMoves);
        return result;
    }
    private static void Repoint(byte[] blob, int[] placed, Dictionary<int, int> textureMoves)
    {
        foreach (int node in placed)
        {
            if (node <= 0 || node + NodeTexturePointer + 4 > blob.Length)
            {
                continue;
            }
            int value = (int)BinaryRead.U32(blob, node + NodeTexturePointer);
            if (textureMoves.TryGetValue(value, out int moved))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(node + NodeTexturePointer), (uint)moved);
            }
        }
    }
    private sealed class Layout
    {
        public required int[] NodeOffsets { get; init; }
        public required int[] NodeEnds { get; init; }
        public required int[] TextureOffsets { get; init; }
        public required Dictionary<int, int> TextureSizes { get; init; }
        public required HashSet<int> MeshNodes { get; init; }
        public required List<int> BoneOrdinals { get; init; }
        public static Layout Read(ReadOnlySpan<byte> blob)
        {
            if (!RingModel.LooksLikeModel(blob))
            {
                throw new ArgumentException("not a model blob", nameof(blob));
            }
            int nodeCount = (int)BinaryRead.U32(blob, 4);
            int textureCount = (int)BinaryRead.U32(blob, 0x0C);
            int[] nodeOffsets = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                nodeOffsets[i] = (int)BinaryRead.U32(blob, RingModel.NodeTableOffset + (i * 4));
            }
            int[] textureOffsets = new int[textureCount];
            for (int i = 0; i < textureCount; i++)
            {
                textureOffsets[i] = (int)BinaryRead.U32(blob, TextureTable + (i * 4));
            }
            int limit = blob.Length;
            int[] bounds =
            [
                .. nodeOffsets.Concat(textureOffsets).Where(o => o > 0).Append(limit).Distinct().Order(),
            ];
            int[] ends = new int[nodeCount];
            List<int> meshes = [];
            List<int> bones = [];
            for (int i = 0; i < nodeCount; i++)
            {
                if (nodeOffsets[i] <= 0)
                {
                    continue;
                }
                ends[i] = bounds.First(b => b > nodeOffsets[i]);
                bool isMesh = nodeOffsets[i] + 0x74 <= limit &&
                    blob.Slice(nodeOffsets[i] + 0x70, 4).SequenceEqual("Mate"u8);
                (isMesh ? meshes : bones).Add(i);
            }
            Dictionary<int, int> sizes = [];
            foreach (int texture in textureOffsets.Where(o => o > 0).Distinct().Order())
            {
                sizes[texture] = bounds.First(b => b > texture) - texture;
            }
            return new Layout
            {
                NodeOffsets = nodeOffsets,
                NodeEnds = ends,
                TextureOffsets = textureOffsets,
                TextureSizes = sizes,
                MeshNodes = [.. meshes],
                BoneOrdinals = bones,
            };
        }
    }
}
