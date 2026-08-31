namespace Omega.Plugins.Gltf;
/// <summary>One run of triangles in a glTF mesh, drawn with a single material.</summary>
public sealed class GltfPrimitive
{
    /// <summary>Name of the material this run uses.</summary>
    public string MaterialName { get; init; } = string.Empty;
    /// <summary>Index of that material in the document, or -1 when it has none.</summary>
    public int MaterialIndex { get; init; } = -1;
    /// <summary>Triangle indices into the owning mesh&apos;s vertex arrays.</summary>
    public int[] Indices { get; init; } = [];
    /// <summary>Triangles in this run.</summary>
    public int TriangleCount => Indices.Length / 3;
}
/// <summary>A glTF mesh flattened to parallel vertex arrays with its runs kept separate.</summary>
public sealed class GltfMesh
{
    /// <summary>Mesh name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Vertex positions, three floats each. This array sets the vertex count.</summary>
    public float[] Positions { get; init; } = [];
    /// <summary>Vertex normals, three floats each, or empty for none.</summary>
    public float[] Normals { get; init; } = [];
    /// <summary>Texture coordinates, two floats each, or empty for none.</summary>
    public float[] Uvs { get; init; } = [];
    /// <summary>Vertex colours, four bytes each in RGBA order, or empty for none.</summary>
    public byte[] Colors { get; init; } = [];
    /// <summary>Skinning joint indices, four per vertex, or empty when the mesh is rigid.</summary>
    public int[] Joints { get; init; } = [];
    /// <summary>Skinning weights, four per vertex, or empty when the mesh is rigid.</summary>
    public float[] Weights { get; init; } = [];
    /// <summary>The runs covering this mesh.</summary>
    public IReadOnlyList<GltfPrimitive> Primitives { get; init; } = [];
    /// <summary>How many vertices the mesh holds.</summary>
    public int VertexCount => Positions.Length / 3;
    /// <summary>Triangles across every run.</summary>
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
    /// <summary>Whether any vertex is actually bound to a joint, rather than merely carrying empty weight arrays.</summary>
    public bool IsSkinned => Array.Exists(Weights, w => w > 0f);
    /// <summary>The mesh name with its vertex and triangle counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
/// <summary>A glTF material, reduced to the parts this application uses.</summary>
public sealed class GltfMaterial
{
    /// <summary>Material name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>The base colour texture, already decoded, or null when the material has none.</summary>
    public ImageData? Image { get; init; }
}
/// <summary>A glTF file read into memory in full.</summary>
public sealed class GltfDocument
{
    /// <summary>The meshes, in file order.</summary>
    public IReadOnlyList<GltfMesh> Meshes { get; init; } = [];
    /// <summary>The materials the runs index into.</summary>
    public IReadOnlyList<GltfMaterial> Materials { get; init; } = [];
    /// <summary>Skeleton joint names, in the order the skin declares them.</summary>
    public IReadOnlyList<string> JointNames { get; init; } = [];
    /// <summary>Mesh, material and joint counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {Materials.Count} material(s), {JointNames.Count} joint(s)";
    }
}
