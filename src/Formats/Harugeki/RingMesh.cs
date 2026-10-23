using Omega.Plugins;
namespace Harugeki.Formats;
public sealed class RingMesh
{
    private const int MaxMaterials = 16;
    private const int MaterialTableOffset = 0x30;
    private const int MaterialRecordOffset = 0x70;
    private const int PositionArrayOffset = 0xDC;
    private const int SkinVertexStride = 24;
    public int NodeIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<RingMaterial> Materials { get; } = [];
    public int[] TriangleCounts { get; init; } = [];
    public int TriangleCount => TriangleCounts.Sum();
    public float[] SkinPositions { get; init; } = [];
    public float[] SkinNormals { get; init; } = [];
    public float[] Positions { get; init; } = [];
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = [];
    public byte[] Colors { get; init; } = [];
    public int[] VertexIds { get; init; } = [];
    public int VertexCount => VertexIds.Length;
    public static RingMesh Parse(ReadOnlySpan<byte> blob, int off, int end, int index)
    {
        if (off < 0 || end > blob.Length || off + MaterialTableOffset + (MaxMaterials * 4) > end)
        {
            throw new ArgumentException("mesh header is outside its blob");
        }
        int[] counts = ReadTriangleCounts(blob, off, end);
        int triangles = counts.Sum();
        int countsOff = FindCountBlock(blob, off, end, triangles);
        int skinVertexCount = (int)BinaryRead.U32(blob, countsOff);
        int positionsOff = countsOff + PositionArrayOffset;
        if (positionsOff + ((long)skinVertexCount * SkinVertexStride) > end)
        {
            throw new ArgumentException("mesh arrays overrun");
        }
        (float[] skinPositions, float[] skinNormals) = ReadSkinArrays(blob, positionsOff, skinVertexCount, end);
        DrawStream draw = ReadDrawStream(
            blob, positionsOff + (skinVertexCount * SkinVertexStride), end, triangles);
        RingMesh mesh = new()
        {
            NodeIndex = index,
            Name = BinaryRead.Name(blob, off, 0x20),
            TriangleCounts = counts,
            SkinPositions = skinPositions,
            SkinNormals = skinNormals,
            Positions = draw.Positions,
            Normals = draw.Normals,
            Uvs = draw.Uvs,
            Colors = draw.Colors,
            VertexIds = draw.VertexIds,
        };
        mesh.Materials.AddRange(ReadMaterials(blob, off, countsOff, counts.Length));
        return mesh;
    }
    private static int[] ReadTriangleCounts(ReadOnlySpan<byte> blob, int off, int end)
    {
        List<int> counts = [];
        for (int i = 0; i < MaxMaterials; i++)
        {
            int at = off + MaterialTableOffset + (i * 4);
            if (at + 4 > end)
            {
                break;
            }
            int c = (int)BinaryRead.U32(blob, at);
            if (c == 0)
            {
                break;
            }
            counts.Add(c);
        }
        return counts.Count == 0 ? throw new ArgumentException("mesh has no triangles") : [.. counts];
    }
    private static int FindCountBlock(ReadOnlySpan<byte> blob, int off, int end, int triangles)
    {
        for (int a = off + MaterialRecordOffset; a + 16 <= System.Math.Min(off + 0x400, end); a += 4)
        {
            if (BinaryRead.U32(blob, a) == BinaryRead.U32(blob, a + 4) &&
                BinaryRead.U32(blob, a) is > 0 and < (1 << 20) &&
                BinaryRead.U32(blob, a + 8) == triangles)
            {
                return a;
            }
        }
        throw new ArgumentException("mesh count block not found");
    }
    private static (float[] Positions, float[] Normals) ReadSkinArrays(
        ReadOnlySpan<byte> blob, int positionsOff, int count, int end)
    {
        if (positionsOff + ((long)count * SkinVertexStride) > end)
        {
            throw new ArgumentException(
                $"skin arrays want {count} vertex record(s) of {SkinVertexStride} byte(s), past the end of the blob");
        }
        float[] positions = new float[checked(count * 3)];
        float[] normals = new float[checked(count * 3)];
        int normalsOff = positionsOff + (count * 12);
        for (int i = 0; i < positions.Length; i++)
        {
            bool mirrored = i % 3 is 0 or 2;
            float p = BinaryRead.F32(blob, positionsOff + (i * 4));
            float n = BinaryRead.F32(blob, normalsOff + (i * 4));
            positions[i] = mirrored ? -p : p;
            normals[i] = mirrored ? -n : n;
        }
        return (positions, normals);
    }
    private static List<RingMaterial> ReadMaterials(
        ReadOnlySpan<byte> blob, int off, int countsOff, int count)
    {
        List<RingMaterial> materials = new(count);
        for (int i = 0; i < count; i++)
        {
            int p = off + MaterialRecordOffset + (RingModel.MaterialStride * i);
            if (p + RingModel.MaterialStride > countsOff)
            {
                break;
            }
            materials.Add(new RingMaterial
            {
                Name = BinaryRead.Name(blob, p, 0x20),
                TextureIndex = BinaryRead.U32(blob, p + 0x20),
                Color =
                [
                    BinaryRead.F32(blob, p + 0x24), BinaryRead.F32(blob, p + 0x28),
                    BinaryRead.F32(blob, p + 0x2C), BinaryRead.F32(blob, p + 0x30),
                ],
            });
        }
        return materials;
    }
    private static DrawStream ReadDrawStream(ReadOnlySpan<byte> blob, int at, int end, int triangles)
    {
        int n = triangles * 3;
        const int FaceStride = (3 * RingModel.VertexStride) + RingModel.FaceTrailer;
        if ((long)triangles * FaceStride > end - at)
        {
            throw new ArgumentException(
                $"draw stream wants {triangles} triangle(s) where {(end - at) / FaceStride} fit");
        }
        float[] positions = new float[checked(n * 3)];
        float[] normals = new float[checked(n * 3)];
        float[] uvs = new float[checked(n * 2)];
        byte[] colors = new byte[checked(n * 4)];
        int[] ids = new int[n];
        for (int f = 0; f < triangles; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                if (at + RingModel.VertexStride > end)
                {
                    throw new ArgumentException("draw stream overran");
                }
                int j = (f * 3) + k;
                for (int c = 0; c < 3; c++)
                {
                    bool mirrored = c is 0 or 2;
                    float p = BinaryRead.F32(blob, at + (c * 4));
                    float normal = BinaryRead.F32(blob, at + 0x0C + (c * 4));
                    positions[(j * 3) + c] = mirrored ? -p : p;
                    normals[(j * 3) + c] = mirrored ? -normal : normal;
                }
                uvs[(j * 2) + 0] = BinaryRead.F32(blob, at + 0x18);
                uvs[(j * 2) + 1] = BinaryRead.F32(blob, at + 0x1C);
                colors[(j * 4) + 0] = blob[at + 0x22];
                colors[(j * 4) + 1] = blob[at + 0x21];
                colors[(j * 4) + 2] = blob[at + 0x20];
                colors[(j * 4) + 3] = blob[at + 0x23];
                ids[j] = BinaryRead.U16(blob, at + 0x24);
                at += RingModel.VertexStride;
            }
            at += RingModel.FaceTrailer;
        }
        return new DrawStream(positions, normals, uvs, colors, ids);
    }
    private readonly record struct DrawStream(
        float[] Positions, float[] Normals, float[] Uvs, byte[] Colors, int[] VertexIds);
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
