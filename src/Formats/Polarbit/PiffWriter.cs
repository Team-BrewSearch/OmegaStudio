using System.Buffers.Binary;
using System.Numerics;
using Omega.Plugins.Gltf;
namespace Polarbit.Formats;
internal static class PiffWriter
{
    public static (byte[] Payload, List<string> Notes) Replace(byte[] original, GltfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PiffModel model = PiffModel.Parse(original);
        List<PiffChunk> chunks = Piff.Walk(original);
        EngineMesh mesh = GltfInput.Merge([.. document.Meshes.Select(GltfInput.Convert)]);
        List<string> notes = [];
        int count = mesh.VertexCount;
        Le.Need(count > 0 && mesh.TriangleCount > 0, "The glTF file has no triangles to import.");
        Le.Need(count <= ushort.MaxValue, $"{count} vertices exceeds the 3DOB limit of {ushort.MaxValue}; the indices are 16-bit. Split or decimate the mesh.");
        if (document.Meshes.Count > 1)
        {
            notes.Add($"{document.Meshes.Count} glTF meshes were merged into the model's single mesh");
        }
        List<string?> appearanceTextures = [.. model.Appearances.Select(t => t >= 0 && t < model.Textures.Count ? model.Textures[t] : null)];
        List<(int Appearance, int[] Indices)> polys = [];
        for (int g = 0; g < mesh.Groups.Count; g++)
        {
            int appearance = appearanceTextures.Count == 0 ? model.Polys.FirstOrDefault().Appearance : GltfInput.Slot(mesh.Groups[g].Material, g, appearanceTextures) + 1;
            polys.Add((appearance, mesh.Groups[g].Indices));
        }
        if (appearanceTextures.Count > 0 && mesh.Groups.Count > appearanceTextures.Count && !mesh.Groups.All(g => appearanceTextures.Any(t => string.Equals(Path.GetFileNameWithoutExtension(t), g.Material, StringComparison.OrdinalIgnoreCase))))
        {
            notes.Add($"the file has {mesh.Groups.Count} materials and the model {appearanceTextures.Count}; the surplus draw with the last");
        }
        bool hasNormals = model.Normals.Length > 0, hasUvs = model.Uvs.Length > 0, hasColours = model.Colors.Length > 0;
        byte[]? skin = model.IsSkinned ? SkinForm(model, mesh, document.JointNames, original, chunks, notes) : null;
        int[] head = [.. model.Head];
        head[0] = polys.Count;
        head[1] = model.IsSkinned ? count * 2 : count;
        head[3] = hasNormals ? count : 0;
        head[5] = hasUvs ? count : 0;
        head[9] = polys.Sum(p => ((p.Indices.Length * 2) + 3) & ~3);
        head[10] = hasColours ? count : 0;
        using MemoryStream output = new();
        output.Write("PIFF\0\0\0\03DOB"u8);
        bool polysWritten = false;
        for (int i = 0; i < chunks.Count; i++)
        {
            PiffChunk chunk = chunks[i];
            if (chunk.Parent != -1)
            {
                continue;
            }
            switch (chunk.Id)
            {
                case "HEAD":
                    Chunk(output, "HEAD", head.SelectMany(BitConverter.GetBytes).ToArray());
                    break;
                case "MESH":
                    Chunk(output, "MESH", Fixed(mesh.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z })));
                    break;
                case "NORM":
                    Chunk(output, "NORM", Fixed(mesh.Normals.SelectMany(n => new[] { n.X, n.Y, n.Z })));
                    break;
                case "TXUV":
                    Chunk(output, "TXUV", Fixed(mesh.Uvs.SelectMany(t => new[] { t.X, t.Y })));
                    break;
                case "COLR":
                    Chunk(output, "COLR", [.. Enumerable.Range(0, count).SelectMany(v => mesh.Colors.Skip(v * 4).Take(model.ColorBytes))]);
                    break;
                case "POLY":
                    if (!polysWritten)
                    {
                        foreach ((int appearance, int[] indices) in polys)
                        {
                            byte[] data = new byte[16 + (indices.Length * 2)];
                            BinaryPrimitives.WriteUInt32LittleEndian(data, 4);
                            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)indices.Length);
                            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)appearance);
                            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 5123);
                            for (int k = 0; k < indices.Length; k++)
                            {
                                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + (2 * k)), (ushort)indices[k]);
                            }
                            Chunk(output, "POLY", data);
                        }
                        polysWritten = true;
                    }
                    break;
                case "FORM:SKIN":
                    output.Write(skin);
                    break;
                case "FORM:LOD ":
                    notes.Add($"the {model.LodLevels} lower levels of detail were dropped, so the new mesh shows at every distance");
                    break;
                default:
                    output.Write(original.AsSpan(chunk.Header, 8 + chunk.Size + (chunk.Id.StartsWith("FORM:", StringComparison.Ordinal) ? 4 : 0)));
                    break;
            }
        }
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        PiffModel check = PiffModel.Parse(result);
        Le.Need(check.VertexCount == count && (!check.IsSkinned || check.Influences.All(i => i.Count > 0)), "The rebuilt model did not read back.");
        return (result, notes);
    }
    private static void Chunk(MemoryStream output, string id, byte[] data)
    {
        output.Write(System.Text.Encoding.Latin1.GetBytes(id));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        output.Write(size);
        output.Write(data);
    }
    private static byte[] Fixed(IEnumerable<float> values)
    {
        return [.. values.SelectMany(v => BitConverter.GetBytes(Le.ToFixed(v)))];
    }
    private static byte[] SkinForm(PiffModel model, EngineMesh mesh, IReadOnlyList<string> jointNames, byte[] original, List<PiffChunk> chunks, List<string> notes)
    {
        List<PiffBone> bones = model.Bones;
        int[] map = new int[jointNames.Count];
        List<string> unmatched = [];
        for (int j = 0; j < map.Length; j++)
        {
            string name = jointNames[j];
            int bone = bones.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (bone < 0)
            {
                int cut = name.LastIndexOf('_');
                bone = cut > 0 ? bones.FindIndex(b => string.Equals(b.Name, name[..cut], StringComparison.OrdinalIgnoreCase)) : -1;
            }
            if (bone < 0)
            {
                unmatched.Add(name);
                bone = j < bones.Count ? j : -1;
            }
            map[j] = bone;
        }
        if (unmatched.Count > 0)
        {
            notes.Add($"{unmatched.Count} joint(s) had no bone of the same name and were matched by position: {string.Join(", ", unmatched.Take(6))}{(unmatched.Count > 6 ? ", …" : string.Empty)}");
        }
        int root = Math.Max(0, bones.FindIndex(b => b.Parent == uint.MaxValue));
        List<int>[] rigid = [.. bones.Select(_ => new List<int>())];
        List<(int Vertex, float Weight)>[] weighted = [.. bones.Select(_ => new List<(int, float)>())];
        int unbound = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            Dictionary<int, float> influence = [];
            for (int k = 0; k < 4; k++)
            {
                float w = mesh.Weights[(v * 4) + k];
                int joint = mesh.Joints[(v * 4) + k];
                if (w > 0 && joint >= 0 && joint < map.Length && map[joint] >= 0)
                {
                    influence[map[joint]] = influence.GetValueOrDefault(map[joint]) + w;
                }
            }
            float sum = influence.Values.Sum();
            if (influence.Count == 0 || sum <= 0)
            {
                rigid[root].Add(v);
                unbound++;
            }
            else if (influence.Count == 1 || influence.Values.Max() / sum >= 0.999f)
            {
                rigid[influence.MaxBy(x => x.Value).Key].Add(v);
            }
            else
            {
                foreach ((int bone, float w) in influence)
                {
                    weighted[bone].Add((v, w / sum));
                }
            }
        }
        if (unbound > 0)
        {
            notes.Add(unbound == mesh.VertexCount
                ? "the file carries no skin, so every vertex follows the root bone"
                : $"{unbound} vertices had no skin weights and follow the root bone");
        }
        List<int> indices = [];
        List<int> weights = [];
        byte[] skel = new byte[4 + (40 * bones.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(skel, (uint)bones.Count);
        PiffChunk oldSkel = Piff.Child(chunks, Piff.IndexOf(chunks, -1, "FORM:SKIN"), "SKEL")!.Value;
        for (int b = 0; b < bones.Count; b++)
        {
            Span<byte> record = skel.AsSpan(4 + (40 * b), 40);
            original.AsSpan(oldSkel.Data + 4 + (40 * b), 40).CopyTo(record);
            int rigidStart = rigid[b].Count > 0 ? indices.Count : -1;
            indices.AddRange(rigid[b]);
            int weightedStart = weighted[b].Count > 0 ? indices.Count : -1;
            int weightStart = weighted[b].Count > 0 ? weights.Count : -1;
            indices.AddRange(weighted[b].Select(x => x.Vertex));
            weights.AddRange(weighted[b].Select(x => Le.ToFixed(x.Weight)));
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], rigid[b].Count);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], weighted[b].Count);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], rigidStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], weightedStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], weightStart);
        }
        int weightsAt = weights.Count > 0 ? (12 + (2 * indices.Count) + 3) & ~3 : 12 + (2 * indices.Count);
        byte[] jnts = new byte[weightsAt + (4 * weights.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(jnts, (uint)indices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(jnts.AsSpan(4), (uint)weights.Count);
        for (int i = 0; i < indices.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(jnts.AsSpan(12 + (2 * i)), (ushort)indices[i]);
        }
        for (int i = 0; i < weights.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(jnts.AsSpan(weightsAt + (4 * i)), weights[i]);
        }
        using MemoryStream form = new();
        form.Write("FORM\0\0\0\0SKIN"u8);
        Chunk(form, "JNTS", jnts);
        Chunk(form, "SKEL", skel);
        byte[] result = form.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }
}
