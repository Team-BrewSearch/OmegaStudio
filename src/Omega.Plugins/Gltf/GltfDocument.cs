namespace Omega.Plugins.Gltf;

public sealed class GltfPrimitive
{
    public string MaterialName { get; init; } = string.Empty;
    public int MaterialIndex { get; init; } = -1;
    public int[] Indices { get; init; } = [];
    public int TriangleCount => Indices.Length / 3;
}
public sealed class GltfMesh
{
    public string Name { get; init; } = string.Empty;
    public float[] Positions { get; init; } = [];
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = [];
    public byte[] Colors { get; init; } = [];
    public int[] Joints { get; init; } = [];
    public float[] Weights { get; init; } = [];
    public IReadOnlyList<GltfPrimitive> Primitives { get; init; } = [];
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Primitives.Count; i++)
            {
                total += Primitives[i].TriangleCount;
            }
            return total;
        }
    }
    public bool IsSkinned => Array.Exists(Weights, w => w > 0f);
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
public sealed class GltfMaterial
{
    public string Name { get; init; } = string.Empty;
    public ImageData? Image { get; init; }
}
public sealed class GltfDocument
{
    public IReadOnlyList<GltfMesh> Meshes { get; init; } = [];
    public IReadOnlyList<GltfMaterial> Materials { get; init; } = [];
    public IReadOnlyList<string> JointNames { get; init; } = [];
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {Materials.Count} material(s), {JointNames.Count} joint(s)";
    }
}
