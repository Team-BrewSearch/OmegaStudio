using System.Text.Json.Nodes;
namespace Omega.Plugins;
public sealed record SceneSubmesh(
    string Name, int First, int Count, int TextureIndex, bool AlphaClip = false)
{
    public IReadOnlyDictionary<string, JsonNode>? Extras { get; init; }
}
public sealed class SceneMesh
{
    public string Name { get; init; } = string.Empty;
    public float[] Positions { get; init; } = [];
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = [];
    public byte[] Colors { get; init; } = [];
    public int[]? Indices { get; init; }
    public int[]? Joints { get; init; }
    public float[]? Weights { get; init; }
    public IReadOnlyList<SceneSubmesh> Submeshes { get; init; } = [];
    public int VertexCount => Positions.Length / 3;
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
    public override string ToString()
    {
        return $"{Name} v={VertexCount} f={TriangleCount}";
    }
}
public enum SceneLighting
{
    Shaded,
    VertexBaked,
}
public enum TextureFiltering
{
    Smooth,
    Nearest,
}
public sealed record SceneJoint(string Name, int ParentIndex, float[] Bind);
public sealed class SceneGeometry
{
    public IReadOnlyList<SceneMesh> Meshes { get; init; } = [];
    public IReadOnlyList<ImageData> Textures { get; init; } = [];
    public SceneLighting Lighting { get; init; } = SceneLighting.Shaded;
    public TextureFiltering Filtering { get; init; } = TextureFiltering.Smooth;
    public IReadOnlyList<SceneJoint> Joints { get; init; } = [];
    public IReadOnlyList<AnimationClip> Clips { get; init; } = [];
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
    public override string ToString()
    {
        return $"{Meshes.Count} mesh(es), {TriangleCount} triangles, {Textures.Count} texture(s)";
    }
}
