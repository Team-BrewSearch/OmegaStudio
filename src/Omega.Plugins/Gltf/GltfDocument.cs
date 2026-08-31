namespace Omega.Plugins.Gltf;

/// <summary>One triangle list within a mesh, sharing a single material.</summary>
public sealed class GltfPrimitive
{
    /// <summary>
    /// The material's name as authored. Omega Studio's own exporter puts an
    /// identifying token here, which is what lets an import land geometry back on
    /// the right texture slot instead of guessing from pixels.
    /// </summary>
    public string MaterialName { get; init; } = string.Empty;

    /// <summary>Index of the material in <see cref="GltfDocument.Materials"/>, or -1.</summary>
    public int MaterialIndex { get; init; } = -1;

    /// <summary>Vertex index triples into the parent mesh's arrays.</summary>
    public int[] Indices { get; init; } = [];

    /// <summary>Triangles this primitive draws.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// One glTF mesh, flattened: every primitive's vertices merged into one array and
/// baked into world space, with the primitives reduced to index ranges over it.
///
/// <para>
/// Flattened because that is the shape a game model wants. A mesh node there has
/// one array of authored vertices that the bone weight lists index into, plus a
/// triangle stream grouped by material — which is exactly a merged vertex array and
/// per-material index ranges. A plugin needing the unmerged, per-primitive form
/// instead should read the file through <see cref="IGltfSource"/>.
/// </para>
/// </summary>
public sealed class GltfMesh
{
    /// <summary>The mesh's name, taken from the node or the mesh itself.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Three floats per vertex, world-space, in glTF's right-handed layout.</summary>
    public float[] Positions { get; init; } = [];
    /// <summary>Three floats per vertex, world-space. Unit Z where the file had none.</summary>
    public float[] Normals { get; init; } = [];

    /// <summary>Two floats per vertex.</summary>
    public float[] Uvs { get; init; } = [];

    /// <summary>Four bytes per vertex, RGBA.</summary>
    public byte[] Colors { get; init; } = [];

    /// <summary>Four joint indices per vertex, into <see cref="GltfDocument.JointNames"/>.</summary>
    public int[] Joints { get; init; } = [];

    /// <summary>Four weights per vertex, matching <see cref="Joints"/>.</summary>
    public float[] Weights { get; init; } = [];

    /// <summary>Index ranges over this mesh's vertices, one per material.</summary>
    public IReadOnlyList<GltfPrimitive> Primitives { get; init; } = [];

    /// <summary>Vertices in the merged arrays.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Triangles across every primitive.</summary>
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

    /// <summary>Whether anything is actually bound to a joint.</summary>
    public bool IsSkinned => Array.Exists(Weights, w => w > 0f);

    /// <summary>The mesh's name and counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}

/// <summary>A material, with its base-colour image decoded if the file embedded one.</summary>
public sealed class GltfMaterial
{
    /// <summary>
    /// The material's name as authored. An importer should match on this before it
    /// resorts to comparing pixels — see the remarks on <see cref="Image"/>.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The base-colour texture, decoded to RGBA, or <see langword="null"/> when the
    /// material has none or the image could not be read.
    /// </summary>
    public ImageData? Image { get; init; }
}

/// <summary>
/// A glTF or GLB file, read into the shape an importer wants.
///
/// <para>
/// Deliberately narrow: skinned triangle geometry, materials and joint names, with
/// each mesh node's world transform baked into its vertices because a game model
/// has no node transform to put it in. Anything that cannot be honoured — sparse
/// accessors, non-triangle primitive modes, a missing POSITION — is refused by name
/// rather than silently dropped, since a quietly half-imported model is worse than
/// one that did not import.
/// </para>
/// </summary>
public sealed class GltfDocument
{
    /// <summary>Every mesh in the scene, flattened and baked into world space.</summary>
    public IReadOnlyList<GltfMesh> Meshes { get; init; } = [];

    /// <summary>The file's materials in document order, images already decoded.</summary>
    public IReadOnlyList<GltfMaterial> Materials { get; init; } = [];

    /// <summary>Joint node names in skin order, for matching against a target skeleton.</summary>
    public IReadOnlyList<string> JointNames { get; init; } = [];

    /// <summary>What the file turned out to contain, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {Materials.Count} material(s), {JointNames.Count} joint(s)";
    }
}
