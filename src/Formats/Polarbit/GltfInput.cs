using System.Numerics;
using Omega.Plugins.Gltf;
namespace Polarbit.Formats;
internal sealed class EngineMesh
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required byte[] Colors { get; init; }
    public required int[] Joints { get; init; }
    public required float[] Weights { get; init; }
    public required List<(string Material, int[] Indices)> Groups { get; init; }
    public int VertexCount => Positions.Length;
    public int TriangleCount => Groups.Sum(g => g.Indices.Length) / 3;
}
internal static class GltfInput
{
    public static EngineMesh Convert(GltfMesh mesh)
    {
        int count = mesh.VertexCount;
        Vector3[] positions = new Vector3[count];
        Vector2[] uvs = new Vector2[count];
        for (int v = 0; v < count; v++)
        {
            (float x, float y, float z) = Axes.FromHost(mesh.Positions[v * 3], mesh.Positions[(v * 3) + 1], mesh.Positions[(v * 3) + 2]);
            positions[v] = new Vector3(x, y, z);
            uvs[v] = mesh.Uvs.Length >= (v * 2) + 2 ? new Vector2(mesh.Uvs[v * 2], Axes.FlipV(mesh.Uvs[(v * 2) + 1])) : Vector2.Zero;
        }
        List<(string, int[])> groups = [.. mesh.Primitives.Where(p => p.Indices.Length >= 3).Select(p => (p.MaterialName, p.Indices[..(p.Indices.Length - (p.Indices.Length % 3))]))];
        bool hasNormals = mesh.Normals.Length == count * 3 && Enumerable.Range(0, count).Any(v => mesh.Normals[v * 3] != 0 || mesh.Normals[(v * 3) + 1] != 0 || mesh.Normals[(v * 3) + 2] != 1);
        Vector3[] normals = new Vector3[count];
        if (hasNormals)
        {
            for (int v = 0; v < count; v++)
            {
                (float x, float y, float z) = Axes.FromHost(mesh.Normals[v * 3], mesh.Normals[(v * 3) + 1], mesh.Normals[(v * 3) + 2]);
                normals[v] = new Vector3(x, y, z);
            }
        }
        else
        {
            foreach ((_, int[] indices) in groups)
            {
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    Vector3 face = Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]);
                    normals[indices[t]] += face;
                    normals[indices[t + 1]] += face;
                    normals[indices[t + 2]] += face;
                }
            }
            for (int v = 0; v < count; v++)
            {
                normals[v] = normals[v].LengthSquared() > 0 ? Vector3.Normalize(normals[v]) : Vector3.UnitY;
            }
        }
        return new EngineMesh
        {
            Name = mesh.Name,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Colors = mesh.Colors.Length == count * 4 ? mesh.Colors : [.. Enumerable.Repeat((byte)255, count * 4)],
            Joints = mesh.Joints.Length == count * 4 ? mesh.Joints : new int[count * 4],
            Weights = mesh.Weights.Length == count * 4 ? mesh.Weights : new float[count * 4],
            Groups = groups,
        };
    }
    public static EngineMesh Merge(IReadOnlyList<EngineMesh> meshes)
    {
        if (meshes.Count == 1)
        {
            return meshes[0];
        }
        List<(string, int[])> groups = [];
        int shift = 0;
        foreach (EngineMesh mesh in meshes)
        {
            int at = shift;
            groups.AddRange(mesh.Groups.Select(g => (g.Material, g.Indices.Select(i => i + at).ToArray())));
            shift += mesh.VertexCount;
        }
        return new EngineMesh
        {
            Name = meshes[0].Name,
            Positions = [.. meshes.SelectMany(m => m.Positions)],
            Normals = [.. meshes.SelectMany(m => m.Normals)],
            Uvs = [.. meshes.SelectMany(m => m.Uvs)],
            Colors = [.. meshes.SelectMany(m => m.Colors)],
            Joints = [.. meshes.SelectMany(m => m.Joints)],
            Weights = [.. meshes.SelectMany(m => m.Weights)],
            Groups = groups,
        };
    }
    public static int Slot(string material, int ordinal, IReadOnlyList<string?> textures)
    {
        for (int i = 0; i < textures.Count; i++)
        {
            if (textures[i] is { } texture && string.Equals(Path.GetFileNameWithoutExtension(texture), material, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return System.Math.Min(ordinal, textures.Count - 1);
    }
}
