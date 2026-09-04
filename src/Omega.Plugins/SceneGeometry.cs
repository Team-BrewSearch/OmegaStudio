using System.Text.Json.Nodes;
namespace Omega.Plugins;
/// <summary>A run of one mesh&apos;s triangles that share a material.</summary>
/// <param name="Name">Label for the run, used as the material name on export.</param>
/// <param name="First">Index of the first entry of the run.</param>
/// <param name="Count">How many entries the run covers. Always a multiple of three.</param>
/// <param name="TextureIndex">Which of the scene&apos;s textures to sample, or -1 for untextured.</param>
/// <param name="AlphaClip">Whether nearly transparent pixels should be discarded rather than blended.</param>
public sealed record SceneSubmesh(
    string Name, int First, int Count, int TextureIndex, bool AlphaClip = false)
{
    /// <summary>Extra glTF material properties to carry through on export, or null.</summary>
    public IReadOnlyDictionary<string, JsonNode>? Extras { get; init; }
}
/// <summary>One mesh: parallel vertex arrays, optional indices, and the runs that divide them by material.</summary>
public sealed class SceneMesh
{
    /// <summary>Mesh name, as the outliner and the export show it.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Vertex positions, three floats each. This array sets the vertex count.</summary>
    public float[] Positions { get; init; } = [];
    /// <summary>Vertex normals, three floats each, or empty for none.</summary>
    public float[] Normals { get; init; } = [];
    /// <summary>Texture coordinates, two floats each, or empty for none.</summary>
    public float[] Uvs { get; init; } = [];
    /// <summary>Vertex colours, four bytes each in RGBA order, or empty for none.</summary>
    public byte[] Colors { get; init; } = [];
    /// <summary>Triangle indices, or null when the positions are already in draw order.</summary>
    public int[]? Indices { get; init; }
    /// <summary>Skinning joint indices, four per vertex, or null when the mesh is rigid.</summary>
    public int[]? Joints { get; init; }
    /// <summary>Skinning weights, four per vertex matching the joints, or null when the mesh is rigid.</summary>
    public float[]? Weights { get; init; }
    /// <summary>The material runs covering this mesh.</summary>
    public IReadOnlyList<SceneSubmesh> Submeshes { get; init; } = [];
    /// <summary>How many vertices the mesh holds.</summary>
    public int VertexCount => Positions.Length / 3;
    /// <summary>Triangles across every run.</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Submeshes.Count; i++)
            {
                total += Submeshes[i].Count / 3;
            }
            return total;
        }
    }
    /// <summary>The mesh name with its vertex and triangle counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
/// <summary>How the viewport should light a scene.</summary>
public enum SceneLighting
{
    /// <summary>Shade from the surface normals.</summary>
    Shaded,
    /// <summary>Take the lighting from the vertex colours, which already carry it. Shading again would darken twice.</summary>
    VertexBaked,
}
/// <summary>How the viewport should sample a scene&apos;s textures.</summary>
public enum TextureFiltering
{
    /// <summary>Interpolate between texels.</summary>
    Smooth,
    /// <summary>Take the nearest texel, keeping low-resolution art crisp.</summary>
    Nearest,
}
/// <summary>One joint of a skeleton.</summary>
/// <param name="Name">Joint name, matched to an animation track.</param>
/// <param name="ParentIndex">Index of the parent joint, or -1 at the root.</param>
/// <param name="Bind">The joint&apos;s bind transform, sixteen floats row by row.</param>
public sealed record SceneJoint(string Name, int ParentIndex, float[] Bind);
/// <summary>A whole scene as the viewport draws it and the exporter writes it: geometry a plugin has already put into a right-handed, Z-up space.</summary>
public sealed class SceneGeometry
{
    /// <summary>The meshes in the scene.</summary>
    public IReadOnlyList<SceneMesh> Meshes { get; init; } = [];
    /// <summary>Textures the material runs index into.</summary>
    public IReadOnlyList<ImageData> Textures { get; init; } = [];
    /// <summary>How the viewport should light this scene.</summary>
    public SceneLighting Lighting { get; init; } = SceneLighting.Shaded;
    /// <summary>How the viewport should sample this scene&apos;s textures.</summary>
    public TextureFiltering Filtering { get; init; } = TextureFiltering.Smooth;
    /// <summary>The skeleton, parents before children. Empty for an unskinned scene.</summary>
    public IReadOnlyList<SceneJoint> Joints { get; init; } = [];
    /// <summary>Clips that drive <see cref="Joints"/>, written out beside the geometry. Empty when the scene holds no animation.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; init; } = [];
    /// <summary>Vertices across every mesh.</summary>
    public int VertexCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Meshes.Count; i++)
            {
                total += Meshes[i].VertexCount;
            }
            return total;
        }
    }
    /// <summary>Triangles across every mesh.</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Meshes.Count; i++)
            {
                total += Meshes[i].TriangleCount;
            }
            return total;
        }
    }
    /// <summary>Checks the scene is internally consistent: array lengths agree with the vertex count, indices and joints stay in range, and every run covers real entries with a real texture. Called before the viewport uploads a scene and before the exporter writes one, so a plugin bug is named here rather than surfacing as corrupt output.</summary>
    /// <exception cref="InvalidDataException">Thrown on the first inconsistency found, naming the mesh.</exception>
    public void Validate()
    {
        for (int m = 0; m < Meshes.Count; m++)
        {
            SceneMesh mesh = Meshes[m];
            string where = $"mesh {m} ('{mesh.Name}')";
            if (mesh.Positions.Length % 3 != 0)
            {
                throw new InvalidDataException($"{where}: position array length {mesh.Positions.Length} is not a multiple of 3.");
            }
            Match(mesh.Normals.Length, mesh.VertexCount * 3, "normals", where);
            Match(mesh.Uvs.Length, mesh.VertexCount * 2, "uvs", where);
            Match(mesh.Colors.Length, mesh.VertexCount * 4, "colors", where);
            Match(mesh.Joints?.Length ?? 0, mesh.VertexCount * 4, "joints", where);
            Match(mesh.Weights?.Length ?? 0, mesh.VertexCount * 4, "weights", where);
            foreach (int joint in mesh.Joints ?? [])
            {
                if (joint < 0 || joint >= Joints.Count)
                {
                    throw new InvalidDataException(
                        $"{where}: joint index {joint} is outside the {Joints.Count} joints.");
                }
            }
            int limit = mesh.Indices?.Length ?? mesh.VertexCount;
            if (mesh.Indices is { } indices)
            {
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= mesh.VertexCount)
                    {
                        throw new InvalidDataException(
                            $"{where}: index {i} is {indices[i]}, outside the {mesh.VertexCount} vertices.");
                    }
                }
            }
            foreach (SceneSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.First < 0 || submesh.Count < 0 || submesh.First + submesh.Count > limit)
                {
                    throw new InvalidDataException(
                        $"{where}: submesh '{submesh.Name}' covers [{submesh.First}, " +
                        $"{submesh.First + submesh.Count}) of only {limit} entries.");
                }
                if (submesh.Count % 3 != 0)
                {
                    throw new InvalidDataException(
                        $"{where}: submesh '{submesh.Name}' has {submesh.Count} entries, not a multiple of 3.");
                }
                if (submesh.TextureIndex < -1 || submesh.TextureIndex >= Textures.Count)
                {
                    throw new InvalidDataException(
                        $"{where}: submesh '{submesh.Name}' wants texture {submesh.TextureIndex}; " +
                        $"only 0-{Textures.Count - 1} exist, and -1 means untextured.");
                }
            }
        }
        static void Match(int actual, int expected, string what, string where)
        {
            if (actual != 0 && actual != expected)
            {
                throw new InvalidDataException(
                    $"{where}: {what} array has {actual} entries, expected {expected} or none.");
            }
        }
    }
    /// <summary>Mesh, triangle and texture counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {TriangleCount} triangles, {Textures.Count} texture(s)";
    }
}
