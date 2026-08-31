using Harugeki.Formats.Skinning;
using Omega.Plugins;

namespace Harugeki.Formats.Scene;

/// <summary>
/// Turns a parsed <see cref="RingModel"/> into the neutral geometry the host draws
/// and exports.
///
/// <para>
/// Almost a straight copy, because the format already stores what the contract asks
/// for: a plain triangle list with no index buffer, grouped by material. That is
/// exactly a <see cref="SceneMesh"/> with a null <see cref="SceneMesh.Indices"/> and
/// one <see cref="SceneSubmesh"/> per material, so nothing has to be rebuilt or
/// re-ordered on the way across.
/// </para>
/// </summary>
internal static class ModelScene
{
    public static SceneGeometry Build(RingModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Bone weight lists address bones by node index; the scene addresses joints
        // by position in its own list. One map, built once, keeps the two apart.
        Dictionary<int, int> slotOfNode = [];
        for (int i = 0; i < model.Bones.Count; i++)
        {
            slotOfNode[model.Bones[i].NodeIndex] = i;
        }

        return new SceneGeometry
        {
            Meshes = [.. model.Meshes.Select(mesh => BuildMesh(model, mesh))],
            Textures = [.. model.Textures.Select(ToImage)],
            Joints = [.. model.Bones.Select(bone => new SceneJoint(
                bone.Name,
                slotOfNode.TryGetValue(bone.ParentNodeIndex, out int parent) ? parent : -1,
                bone.Bind))],

            // Lighting and Filtering are left at their defaults: this game lights its
            // models at run time rather than baking it into the vertex colours, and its
            // textures have the resolution to interpolate well.
        };
    }

    public static ImageData ToImage(RingTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return ImageData.Create(
            texture.Name, texture.Width, texture.Height, texture.Pixels, "RGBA8 uncompressed");
    }

    private static SceneMesh BuildMesh(RingModel model, RingMesh mesh)
    {
        List<SceneSubmesh> submeshes = [];
        int first = 0;

        for (int i = 0; i < mesh.TriangleCounts.Length; i++)
        {
            int count = mesh.TriangleCounts[i] * 3;
            if (count == 0)
            {
                continue;
            }

            RingMaterial? material = i < mesh.Materials.Count ? mesh.Materials[i] : null;
            int texture = material is { HasTexture: true } && material.TextureIndex < model.Textures.Count
                ? (int)material.TextureIndex
                : -1;

            submeshes.Add(new SceneSubmesh(material?.Name ?? $"{mesh.Name}_{i}", first, count, texture));
            first += count;
        }

        (int[]? joints, float[]? weights) = BuildSkin(model, mesh);

        return new SceneMesh
        {
            Name = mesh.Name,
            Positions = mesh.Positions,
            Normals = mesh.Normals,
            Uvs = mesh.Uvs,
            Colors = mesh.Colors,

            // Null: the draw stream is already an expanded triangle list, three
            // vertices per face with nothing shared.
            Indices = null,
            Joints = joints,
            Weights = weights,
            Submeshes = submeshes,
        };
    }

    /// <summary>
    /// Expands the format's per-bone vertex lists into the per-vertex influences an
    /// exporter wants, or null for a mesh nothing weights.
    ///
    /// <para>
    /// Two indirections meet here. The bone lists address <i>skin</i> vertices — the
    /// authored positions — while the draw stream is the expanded triangle list, and
    /// <see cref="RingMesh.VertexIds"/> is the map from one to the other. Getting this
    /// backwards produces a model that skins to the wrong vertices, which looks like
    /// the mesh is being torn apart rather than posed.
    /// </para>
    /// </summary>
    private static (int[]? Joints, float[]? Weights) BuildSkin(RingModel model, RingMesh mesh)
    {
        SkinBinding? binding = SkinBinding.Build(model, mesh);
        if (binding is null)
        {
            return (null, null);
        }

        int count = mesh.VertexCount;
        int[] joints = new int[count * 4];
        float[] weights = new float[count * 4];

        for (int i = 0; i < count; i++)
        {
            int skinVertex = mesh.VertexIds[i];
            if (!binding.TryGetInfluences(
                    skinVertex, out ReadOnlySpan<int> bones, out ReadOnlySpan<float> influence))
            {
                continue;
            }

            for (int k = 0; k < SkinBinding.MaxInfluences; k++)
            {
                // A joint index of -1 means "no influence"; the contract requires
                // every index to be in range, so an empty slot points at joint 0 with
                // a weight of zero, which contributes nothing.
                bool used = influence[k] > 0f && bones[k] >= 0 && bones[k] < model.Bones.Count;
                joints[(i * 4) + k] = used ? bones[k] : 0;
                weights[(i * 4) + k] = used ? influence[k] : 0f;
            }
        }

        return (joints, weights);
    }
}
