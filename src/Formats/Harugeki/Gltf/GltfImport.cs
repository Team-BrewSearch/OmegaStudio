using Harugeki.Formats.Skinning;
using Omega.Plugins.Gltf;
namespace Harugeki.Formats.Gltf;
public sealed record GltfImportReport(
    int MeshesReplaced,
    int MeshesKept,
    int Vertices,
    int Triangles,
    int JointsMatched,
    int JointsUnmatched,
    IReadOnlyList<string> Warnings)
{
    public override string ToString()
    {
        string kept = MeshesKept > 0 ? $", {MeshesKept} kept" : string.Empty;
        string joints = JointsUnmatched > 0
            ? $", {JointsMatched}/{JointsMatched + JointsUnmatched} joints matched"
            : $", {JointsMatched} joints matched";
        return $"{MeshesReplaced} mesh(es) replaced{kept}: {Vertices:N0} vertices, {Triangles:N0} triangles{joints}";
    }
}
public static class GltfImport
{
    public static (byte[] Blob, GltfImportReport Report) Apply(
        RingModel target, byte[] targetBlob, GltfDocument scene)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetBlob);
        ArgumentNullException.ThrowIfNull(scene);
        if (target.Meshes.Count == 0)
        {
            throw new InvalidDataException("The selected model has no mesh to replace.");
        }
        List<string> warnings = [];
        int[] jointToBone = MapJoints(target, scene, warnings, out int matched, out int unmatched);
        List<GltfMesh> assigned = Distribute([.. scene.Meshes], target.Meshes.Count, warnings);
        Dictionary<int, ModelGeometry> replacements = [];
        int vertices = 0, triangles = 0;
        for (int i = 0; i < assigned.Count; i++)
        {
            ModelGeometry geometry = Convert(assigned[i], target, i, jointToBone, warnings);
            geometry.Validate($"Mesh '{assigned[i].Name}'");
            replacements[target.Meshes[i].NodeIndex] = geometry;
            vertices += geometry.VertexCount;
            triangles += geometry.TriangleCount;
        }
        byte[] blob = ModelWriter.ReplaceGeometry(targetBlob, replacements);
        return (blob, new GltfImportReport(
            replacements.Count, target.Meshes.Count - replacements.Count,
            vertices, triangles, matched, unmatched, warnings));
    }
    private static List<GltfMesh> Distribute(List<GltfMesh> source, int slots, List<string> warnings)
    {
        if (source.Count <= slots)
        {
            return source;
        }
        warnings.Add(
            $"the file has {source.Count} meshes but the target model has {slots} mesh slot(s); " +
            $"the last {source.Count - slots + 1} were merged into one");
        List<GltfMesh> result = [.. source.Take(slots - 1)];
        result.Add(Merge(source.Skip(slots - 1).ToList()));
        return result;
    }
    private static GltfMesh Merge(List<GltfMesh> meshes)
    {
        List<float> positions = [], normals = [], uvs = [], weights = [];
        List<byte> colors = [];
        List<int> joints = [];
        List<GltfPrimitive> primitives = [];
        foreach (GltfMesh mesh in meshes)
        {
            int shift = positions.Count / 3;
            positions.AddRange(mesh.Positions);
            normals.AddRange(mesh.Normals);
            uvs.AddRange(mesh.Uvs);
            colors.AddRange(mesh.Colors);
            joints.AddRange(mesh.Joints);
            weights.AddRange(mesh.Weights);
            foreach (GltfPrimitive primitive in mesh.Primitives)
            {
                primitives.Add(new GltfPrimitive
                {
                    MaterialName = primitive.MaterialName,
                    MaterialIndex = primitive.MaterialIndex,
                    Indices = [.. primitive.Indices.Select(i => i + shift)],
                });
            }
        }
        return new GltfMesh
        {
            Name = meshes[0].Name,
            Positions = [.. positions],
            Normals = [.. normals],
            Uvs = [.. uvs],
            Colors = [.. colors],
            Joints = [.. joints],
            Weights = [.. weights],
            Primitives = primitives,
        };
    }
    private static int[] MapJoints(
        RingModel target, GltfDocument scene, List<string> warnings, out int matched, out int unmatched)
    {
        Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < target.Bones.Count; i++)
        {
            _ = byName.TryAdd(target.Bones[i].Name, i);
        }
        int[] map = new int[scene.JointNames.Count];
        matched = 0;
        unmatched = 0;
        List<string> missing = [];
        for (int i = 0; i < map.Length; i++)
        {
            if (byName.TryGetValue(scene.JointNames[i], out int bone))
            {
                map[i] = bone;
                matched++;
            }
            else if (i < target.Bones.Count)
            {
                map[i] = i;
                unmatched++;
                missing.Add(scene.JointNames[i]);
            }
            else
            {
                map[i] = -1;
                unmatched++;
                missing.Add(scene.JointNames[i]);
            }
        }
        if (missing.Count > 0)
        {
            warnings.Add(
                $"{missing.Count} joint(s) had no bone of the same name and were matched by position: " +
                string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", …" : string.Empty));
        }
        return map;
    }
    private static ModelGeometry Convert(
        GltfMesh mesh, RingModel target, int slot, int[] jointToBone, List<string> warnings)
    {
        int targetMaterials = slot < target.Meshes.Count
            ? System.Math.Max(1, target.Meshes[slot].Materials.Count)
            : 1;
        List<int[]> groups = [.. mesh.Primitives.Select(p => p.Indices)];
        if (groups.Count == 0)
        {
            throw new InvalidDataException($"Mesh '{mesh.Name}' has no triangles.");
        }
        if (groups.Count > targetMaterials)
        {
            warnings.Add(
                $"'{mesh.Name}' has {groups.Count} materials but the target mesh has {targetMaterials}; " +
                "the surplus were merged into the last");
            List<int[]> merged = [.. groups.Take(targetMaterials - 1)];
            merged.Add([.. groups.Skip(targetMaterials - 1).SelectMany(g => g)]);
            groups = merged;
        }
        Dictionary<int, (List<int> Vertices, List<float> Weights)> perBone = [];
        int dropped = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            for (int k = 0; k < SkinBinding.MaxInfluences; k++)
            {
                float weight = mesh.Weights[(v * 4) + k];
                if (weight <= 0f)
                {
                    continue;
                }
                int joint = mesh.Joints[(v * 4) + k];
                int bone = joint >= 0 && joint < jointToBone.Length ? jointToBone[joint] : -1;
                if (bone < 0)
                {
                    dropped++;
                    continue;
                }
                if (!perBone.TryGetValue(bone, out (List<int> Vertices, List<float> Weights) list))
                {
                    perBone[bone] = list = ([], []);
                }
                list.Vertices.Add(v);
                list.Weights.Add(weight);
            }
        }
        if (dropped > 0)
        {
            warnings.Add($"'{mesh.Name}': {dropped} weight(s) referenced a joint with no bone and were dropped");
        }
        if (perBone.Count == 0 && mesh.IsSkinned)
        {
            warnings.Add($"'{mesh.Name}' is skinned but nothing bound; it will be rigid");
        }
        return new ModelGeometry
        {
            Positions = mesh.Positions,
            Normals = mesh.Normals,
            Uvs = mesh.Uvs,
            Colors = mesh.Colors,
            MaterialIndices = groups,
            BoneWeights = perBone.ToDictionary(
                kv => kv.Key, kv => (kv.Value.Vertices.ToArray(), kv.Value.Weights.ToArray())),
        };
    }
}
