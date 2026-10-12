using System.Numerics;
using System.Text.Json;
using Omega.Plugins.Gltf;
namespace OmegaStudio.Services.Gltf;
internal static class GltfReader
{
    public static GltfDocument Read(string path)
    {
        (JsonElement root, byte[][] buffers) = GltfContainer.Load(path);
        return Build(root, buffers);
    }
    private static GltfDocument Build(JsonElement root, byte[][] buffers)
    {
        JsonElement[] nodes = GltfJson.Array(root, "nodes");
        JsonElement[] meshes = GltfJson.Array(root, "meshes");
        JsonElement[] materials = GltfJson.Array(root, "materials");
        JsonElement[] skins = GltfJson.Array(root, "skins");
        List<string> jointNames = [];
        if (skins.Length > 0 && skins[0].TryGetProperty("joints", out JsonElement joints))
        {
            foreach (JsonElement joint in joints.EnumerateArray())
            {
                int index = joint.GetInt32();
                jointNames.Add(index >= 0 && index < nodes.Length
                    ? GltfJson.Name(nodes[index], $"joint{index}")
                    : $"joint{index}");
            }
        }
        List<GltfMesh> result = [];
        foreach ((int node, Matrix4x4 world) in GltfContainer.WalkScene(root, nodes))
        {
            if (!nodes[node].TryGetProperty("mesh", out JsonElement meshRef))
            {
                continue;
            }
            int meshIndex = meshRef.GetInt32();
            if (meshIndex < 0 || meshIndex >= meshes.Length)
            {
                continue;
            }
            Matrix4x4 bake = nodes[node].TryGetProperty("skin", out _) ? Matrix4x4.Identity : world;
            result.Add(ReadMesh(
                meshes[meshIndex],
                GltfJson.Name(nodes[node], GltfJson.Name(meshes[meshIndex], $"mesh{meshIndex}")),
                bake, root, buffers, materials));
        }
        return result.Count == 0
            ? throw new InvalidDataException("The glTF file contains no mesh with triangles.")
            : new GltfDocument
            {
                Meshes = result,
                Materials = GltfMaterials.Read(root, buffers),
                JointNames = jointNames,
            };
    }
    private static GltfMesh ReadMesh(
        JsonElement mesh, string name, Matrix4x4 world,
        JsonElement root, byte[][] buffers, JsonElement[] materials)
    {
        List<float> positions = [], normals = [], uvs = [], weights = [];
        List<byte> colors = [];
        List<int> joints = [];
        List<GltfPrimitive> primitives = [];
        Dictionary<string, (int First, int Count)> sharedVertices = [];
        Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : Matrix4x4.Identity;
        foreach (JsonElement primitive in GltfJson.Array(mesh, "primitives"))
        {
            if (primitive.TryGetProperty("mode", out JsonElement mode) && mode.GetInt32() != 4)
            {
                throw new InvalidDataException(
                    $"Mesh '{name}' uses primitive mode {mode.GetInt32()}; " +
                    "only triangle lists (mode 4) can be imported.");
            }
            if (!primitive.TryGetProperty("attributes", out JsonElement attributes) ||
                !attributes.TryGetProperty("POSITION", out JsonElement positionRef))
            {
                throw new InvalidDataException($"Mesh '{name}' has a primitive with no POSITION attribute.");
            }
            string signature = Signature(attributes);
            if (sharedVertices.TryGetValue(signature, out (int First, int Count) shared))
            {
                primitives.Add(Primitive(primitive, materials, primitives.Count,
                    ReadIndices(root, buffers, primitive, shared.Count, shared.First)));
                continue;
            }
            int first = positions.Count / 3;
            float[] p = GltfContainer.ReadAccessor(root, buffers, positionRef.GetInt32(), 3);
            int count = p.Length / 3;
            sharedVertices[signature] = (first, count);
            float[] n = Read(root, buffers, attributes, "NORMAL", 3, count);
            float[] uv = Read(root, buffers, attributes, "TEXCOORD_0", 2, count);
            float[] colour = Read(root, buffers, attributes, "COLOR_0", 4, count, fallback: 1f);
            float[] jointIds = Read(root, buffers, attributes, "JOINTS_0", 4, count);
            float[] w = Read(root, buffers, attributes, "WEIGHTS_0", 4, count);
            bool hasNormals = attributes.TryGetProperty("NORMAL", out _);
            for (int i = 0; i < count; i++)
            {
                Vector3 position = Vector3.Transform(
                    new Vector3(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]), world);
                positions.Add(position.X);
                positions.Add(position.Y);
                positions.Add(position.Z);
                Vector3 normal = hasNormals
                    ? Vector3.TransformNormal(
                        new Vector3(n[i * 3], n[(i * 3) + 1], n[(i * 3) + 2]), normalMatrix)
                    : Vector3.UnitZ;
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                normals.Add(normal.X);
                normals.Add(normal.Y);
                normals.Add(normal.Z);
                uvs.Add(uv[i * 2]);
                uvs.Add(uv[(i * 2) + 1]);
                for (int c = 0; c < 4; c++)
                {
                    colors.Add((byte)Math.Clamp((int)MathF.Round(colour[(i * 4) + c] * 255f), 0, 255));
                    joints.Add((int)jointIds[(i * 4) + c]);
                    weights.Add(w[(i * 4) + c]);
                }
            }
            int[] indices = ReadIndices(root, buffers, primitive, count, first);
            if (indices.Length % 3 != 0)
            {
                throw new InvalidDataException(
                    $"Mesh '{name}' has an index count that is not a multiple of three.");
            }
            primitives.Add(Primitive(primitive, materials, primitives.Count, indices));
        }
        return new GltfMesh
        {
            Name = name,
            Positions = [.. positions],
            Normals = [.. normals],
            Uvs = [.. uvs],
            Colors = [.. colors],
            Joints = [.. joints],
            Weights = [.. weights],
            Primitives = primitives,
        };
    }
    private static GltfPrimitive Primitive(
        JsonElement primitive, JsonElement[] materials, int ordinal, int[] indices)
    {
        int index = primitive.TryGetProperty("material", out JsonElement m) && m.TryGetInt32(out int value)
            ? value
            : -1;
        bool known = index >= 0 && index < materials.Length;
        return new GltfPrimitive
        {
            MaterialIndex = known ? index : -1,
            MaterialName = known ? GltfJson.Name(materials[index], $"Material{ordinal:00}") : $"Material{ordinal:00}",
            Indices = indices,
        };
    }
    private static string Signature(JsonElement attributes)
    {
        List<string> parts = [];
        foreach (JsonProperty attribute in attributes.EnumerateObject())
        {
            parts.Add($"{attribute.Name}={attribute.Value.GetInt32()}");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }
    private static int[] ReadIndices(
        JsonElement root, byte[][] buffers, JsonElement primitive, int count, int first)
    {
        if (!primitive.TryGetProperty("indices", out JsonElement reference))
        {
            return [.. Enumerable.Range(first, count)];
        }
        int accessor = reference.GetInt32();
        int[] indices = GltfContainer.ReadIntegers(root, buffers, accessor, 1);
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (index < 0 || index >= count)
            {
                throw new InvalidDataException(
                    $"glTF index {i} is {index}, outside the primitive's {count} vertices.");
            }
        }
        int[] into = new int[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            into[i] = first + indices[i];
        }
        return into;
    }
    private static float[] Read(
        JsonElement root, byte[][] buffers, JsonElement attributes,
        string name, int components, int count, float fallback = 0f)
    {
        if (!attributes.TryGetProperty(name, out JsonElement reference))
        {
            float[] filled = new float[count * components];
            if (fallback != 0f)
            {
                Array.Fill(filled, fallback);
            }
            return filled;
        }
        float[] values = GltfContainer.ReadAccessor(root, buffers, reference.GetInt32(), components);
        if (values.Length == count * components)
        {
            return values;
        }
        float[] padded = new float[count * components];
        values.AsSpan(0, Math.Min(values.Length, padded.Length)).CopyTo(padded);
        return padded;
    }
}
