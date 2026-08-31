using System.Text.Json.Nodes;

namespace Omega.Plugins;

/// <summary>
/// A run of triangles sharing one texture — one draw call.
/// </summary>
/// <param name="Name">For diagnostics and glTF material naming.</param>
/// <param name="First">
/// Where this run starts. An offset into <see cref="SceneMesh.Indices"/> when the
/// mesh is indexed, and into the vertex arrays when it is not.
/// </param>
/// <param name="Count">How many entries the run covers, always a multiple of three.</param>
/// <param name="TextureIndex">
/// Index into <see cref="SceneGeometry.Textures"/>, or -1 for untextured.
/// </param>
/// <param name="AlphaClip">
/// Whether the texture has real transparency and should be alpha-tested rather than
/// drawn opaque.
/// </param>
public sealed record SceneSubmesh(
    string Name, int First, int Count, int TextureIndex, bool AlphaClip = false)
{
    /// <summary>
    /// Opaque values written verbatim into this submesh's glTF <c>extras</c> on
    /// export, and read back on import.
    ///
    /// <para>
    /// For round-trip detail that is genuinely <i>source</i> information — how the
    /// original file chose to batch a run of triangles, say — which no exporter can
    /// re-derive and which nothing else in the scene has anywhere to live. The host
    /// never looks inside; it only carries them. Leave this null unless a format
    /// needs byte-exact reconversion.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode>? Extras { get; init; }
}

/// <summary>
/// One mesh: parallel per-vertex arrays, divided into submeshes by texture.
///
/// <para>
/// <see cref="Indices"/> is what makes this fit both games without either having to
/// pretend. Leave it <see langword="null"/> and the vertex arrays are read as a flat
/// triangle list, which is exactly how the Harugeki draw stream is already stored;
/// set it and the mesh is indexed, which is exactly how a Kartz track is. The host
/// packs both through one path.
/// </para>
/// </summary>
public sealed class SceneMesh
{
    /// <summary>
    /// The mesh's name. Becomes the glTF mesh and node name on export, which some
    /// formats read back to recover where the geometry came from.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Three floats per vertex.</summary>
    public float[] Positions { get; init; } = [];

    /// <summary>Three floats per vertex. May be empty, in which case flat shading is used.</summary>
    public float[] Normals { get; init; } = [];

    /// <summary>Two floats per vertex. May be empty for untextured geometry.</summary>
    public float[] Uvs { get; init; } = [];

    /// <summary>Four bytes per vertex, RGBA. May be empty, in which case white is used.</summary>
    public byte[] Colors { get; init; } = [];

    /// <summary>
    /// Vertex indices, three per triangle, or <see langword="null"/> when
    /// <see cref="Positions"/> is already an expanded triangle list.
    /// </summary>
    public int[]? Indices { get; init; }

    /// <summary>
    /// Four joint indices per vertex into <see cref="SceneGeometry.Joints"/>, or
    /// <see langword="null"/> for unskinned geometry. Exported as glTF JOINTS_0.
    /// </summary>
    public int[]? Joints { get; init; }

    /// <summary>
    /// Four weights per vertex, matching <see cref="Joints"/>. Exported as WEIGHTS_0.
    /// </summary>
    public float[]? Weights { get; init; }

    /// <summary>The runs this mesh is drawn in, one per texture.</summary>
    public IReadOnlyList<SceneSubmesh> Submeshes { get; init; } = [];

    /// <summary>Vertices in the arrays above.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Triangles this mesh draws, counted from its submeshes.</summary>
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

    /// <summary>The mesh's name and counts, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}

/// <summary>Where a scene's lighting comes from.</summary>
public enum SceneLighting
{
    /// <summary>
    /// The viewport lights it, from the vertex normals. The right answer for a model
    /// the game itself lit at run time.
    /// </summary>
    Shaded,

    /// <summary>
    /// <see cref="SceneMesh.Colors"/> already contains the lighting, so the viewport
    /// adds none of its own and simply modulates the texture by it.
    ///
    /// <para>
    /// The distinction is not cosmetic. Doing both — shading a scene whose colours are
    /// already a baked lightmap — darkens it twice, worst wherever a surface sits at a
    /// grazing angle to the camera, which is most of a landscape.
    /// </para>
    /// </summary>
    VertexBaked,
}

/// <summary>How a scene's textures should be sampled.</summary>
public enum TextureFiltering
{
    /// <summary>Interpolated. Right for textures with enough resolution to blur well.</summary>
    Smooth,

    /// <summary>
    /// Nearest texel, no interpolation. Right for the low-resolution, hand-placed
    /// texels of an older console, where interpolation reads as smeared rather than
    /// smooth — and where seeing the actual pixels is usually the point.
    /// </summary>
    Nearest,
}

/// <summary>One bone of a skinned scene.</summary>
/// <param name="Name">
/// The joint's name, which is what an importer matches against to land geometry back
/// on the right bone after a round trip through a modelling tool.
/// </param>
/// <param name="ParentIndex">Index of the parent joint, or -1 for a root.</param>
/// <param name="Bind">
/// Row-major 4x4 <b>world-space</b> bind transform. The exporter inverts these itself
/// to produce glTF's inverse bind matrices.
/// </param>
public sealed record SceneJoint(string Name, int ParentIndex, float[] Bind);

/// <summary>
/// Everything the viewport needs to draw an asset, and everything the glTF exporter
/// needs to write one: meshes, the textures their submeshes index into, and the
/// skeleton binding them if there is one.
/// </summary>
public sealed class SceneGeometry
{
    /// <summary>The geometry, drawn in the order given.</summary>
    public IReadOnlyList<SceneMesh> Meshes { get; init; } = [];

    /// <summary>The textures a submesh's <see cref="SceneSubmesh.TextureIndex"/> selects.</summary>
    public IReadOnlyList<ImageData> Textures { get; init; } = [];

    /// <summary>
    /// Where this scene's lighting comes from. See <see cref="SceneLighting"/>.
    /// </summary>
    public SceneLighting Lighting { get; init; } = SceneLighting.Shaded;

    /// <summary>
    /// How the viewport and an export should sample this scene's textures. See
    /// <see cref="TextureFiltering"/>.
    /// </summary>
    public TextureFiltering Filtering { get; init; } = TextureFiltering.Smooth;

    /// <summary>
    /// The skeleton, when the geometry is skinned. Empty otherwise.
    ///
    /// <para>
    /// Carried by the scene rather than left to the plugin's exporter so that a
    /// skinned model survives a round trip through a modelling tool — export it,
    /// pose it in Blender, and the joints are still named and still bound.
    /// </para>
    /// </summary>
    public IReadOnlyList<SceneJoint> Joints { get; init; } = [];

    /// <summary>Total vertices across every mesh.</summary>
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

    /// <summary>
    /// Checks the geometry is internally consistent, throwing with a message naming
    /// the offending mesh if it is not.
    ///
    /// <para>
    /// The host runs this before uploading to the GPU. A submesh range that runs off
    /// the end of its arrays is a plugin bug that would otherwise surface as a driver
    /// crash or a screenful of garbage triangles, neither of which points at the
    /// plugin that caused it.
    /// </para>
    /// </summary>
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

                // -1 is the documented "untextured"; anything else below zero is a plugin bug
                // that would otherwise be silently read as untextured and draw the wrong thing.
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

    /// <summary>What the scene holds, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {TriangleCount} triangles, {Textures.Count} texture(s)";
    }
}
