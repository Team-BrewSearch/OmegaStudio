using Harugeki.Formats.Skinning;
using Omega.Plugins;
namespace Harugeki.Formats.Scene;
internal static class ModelScene
{
    public static SceneGeometry Build(RingModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
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
            Indices = null,
            Joints = joints,
            Weights = weights,
            Submeshes = submeshes,
        };
    }
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
                bool used = influence[k] > 0f && bones[k] >= 0 && bones[k] < model.Bones.Count;
                joints[(i * 4) + k] = used ? bones[k] : 0;
                weights[(i * 4) + k] = used ? influence[k] : 0f;
            }
        }
        return (joints, weights);
    }
}
