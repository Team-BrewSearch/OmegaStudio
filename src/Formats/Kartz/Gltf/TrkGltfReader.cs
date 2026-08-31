using Omega.Plugins.Gltf;
using System.Numerics;
using System.Text.Json;
namespace Kartz.Formats.Gltf;

public static class TrkGltfReader
{
    public static List<TrkImportPrimitive> Read(
        IGltfSource source, IReadOnlyList<SskTexture> targetTextures, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(log);
        JsonElement root = source.Root;
        JsonElement[] nodes = GltfJson.Array(root, "nodes");
        JsonElement[] meshes = GltfJson.Array(root, "meshes");
        int[] materialToTexture = ResolveMaterialTextures(
            source, targetTextures, out List<string> warnings);
        foreach (string warning in warnings)
        {
            log(warning);
        }
        List<TrkImportPrimitive> result = [];
        foreach ((int node, Matrix4x4 world) in source.WalkScene())
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
            Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inverse)
                ? Matrix4x4.Transpose(inverse)
                : Matrix4x4.Identity;
            int? chunkIndex = TryParseChunkName(nodes[node]) ?? TryParseChunkName(meshes[meshIndex]);
            foreach (JsonElement primitive in GltfJson.Array(meshes[meshIndex], "primitives"))
            {
                result.Add(ReadPrimitive(
                    primitive, source, world, normalMatrix, materialToTexture,
                    targetTextures, chunkIndex));
            }
        }
        return result.Count == 0 ? throw new InvalidDataException("The glTF file contains no mesh with triangles.") : result;
    }
    private static int? TryParseChunkName(JsonElement nodeOrMesh)
    {
        const string prefix = "chunk_";
        if (!nodeOrMesh.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? name = nameEl.GetString();
        return name is null || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(prefix.Length), out int parsed) || parsed < 0
            ? null
            : parsed;
    }
    private static TrkImportPrimitive ReadPrimitive(
        JsonElement primitive, IGltfSource source,
        Matrix4x4 world, Matrix4x4 normalMatrix, int[] materialToTexture,
        IReadOnlyList<SskTexture> targetTextures, int? chunkIndex)
    {
        if (primitive.TryGetProperty("mode", out JsonElement mode) && mode.GetInt32() != 4)
        {
            throw new InvalidDataException($"Primitive mode {mode.GetInt32()} is not a triangle list (mode 4).");
        }
        if (!primitive.TryGetProperty("attributes", out JsonElement attributes)
            || !attributes.TryGetProperty("POSITION", out JsonElement positionRef))
        {
            throw new InvalidDataException("A primitive has no POSITION attribute.");
        }
        float[] p = source.ReadAccessor(positionRef.GetInt32(), 3);
        int count = p.Length / 3;
        float[] n = ReadOrDefault(source, attributes, "NORMAL", 3, count, 0f);
        float[] uv = ReadOrDefault(source, attributes, "TEXCOORD_0", 2, count, 0f);
        float[] col = ReadOrDefault(source, attributes, "COLOR_0", 4, count, 1f);
        bool hasNormals = attributes.TryGetProperty("NORMAL", out _);
        int materialIndex = primitive.TryGetProperty("material", out JsonElement m) ? m.GetInt32() : -1;
        int textureIndex = materialIndex >= 0 && materialIndex < materialToTexture.Length
            ? materialToTexture[materialIndex]
            : -1;
        float texW = 1f, texH = 1f;
        if (textureIndex >= 0 && textureIndex < targetTextures.Count)
        {
            texW = Math.Max(1, targetTextures[textureIndex].Width);
            texH = Math.Max(1, targetTextures[textureIndex].Height);
        }
        float[] positions = new float[count * 3];
        float[] normals = new float[count * 3];
        float[] uvs = new float[count * 2];
        byte[] colors = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = Vector3.Transform(new Vector3(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]), world);
            positions[(i * 3) + 0] = pos.X;
            positions[(i * 3) + 1] = pos.Y;
            positions[(i * 3) + 2] = pos.Z;
            Vector3 nrm = hasNormals
                ? Vector3.TransformNormal(new Vector3(n[i * 3], n[(i * 3) + 1], n[(i * 3) + 2]), normalMatrix)
                : Vector3.UnitZ;
            nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
            normals[(i * 3) + 0] = nrm.X;
            normals[(i * 3) + 1] = nrm.Y;
            normals[(i * 3) + 2] = nrm.Z;
            uvs[(i * 2) + 0] = uv[i * 2] * texW;
            uvs[(i * 2) + 1] = uv[(i * 2) + 1] * texH;
            for (int c = 0; c < 4; c++)
            {
                colors[(i * 4) + c] = (byte)Math.Clamp((int)MathF.Round(col[(i * 4) + c] * 255f), 0, 255);
            }
        }
        int[] indices = ReadIndices(source, primitive, count);
        return indices.Length % 3 != 0
            ? throw new InvalidDataException("A primitive's index count is not a multiple of three.")
            : new TrkImportPrimitive
            {
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Colors = colors,
                Indices = indices,
                TextureIndex = textureIndex,
                ChunkIndex = chunkIndex,
                Blocks = TryReadBlocks(primitive, indices.Length / 3),
                TexcoordEmit = TryReadTexcoordEmit(primitive),
                GroupBreaks = TryReadGroupBreaks(primitive),
            };
    }
    private static List<int>? TryReadGroupBreaks(JsonElement primitive)
    {
        if (!primitive.TryGetProperty("extras", out JsonElement extras)
            || extras.ValueKind != JsonValueKind.Object
            || !extras.TryGetProperty("kartzGroupBreaks", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        List<int> breaks = [];
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (!el.TryGetInt32(out int v) || v <= 0)
            {
                return null;
            }
            breaks.Add(v);
        }
        return breaks.Count > 0 ? breaks : null;
    }
    private static bool[]? TryReadTexcoordEmit(JsonElement primitive)
    {
        if (!primitive.TryGetProperty("extras", out JsonElement extras)
            || extras.ValueKind != JsonValueKind.Object
            || !extras.TryGetProperty("kartzTexcoordEmit", out JsonElement flags)
            || flags.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? s = flags.GetString();
        return string.IsNullOrEmpty(s) || s.Any(c => c is not ('0' or '1')) ? null : [.. s.Select(c => c == '1')];
    }
    private static List<(int Mode, int TriangleCount)>? TryReadBlocks(JsonElement primitive, int triangleCount)
    {
        if (!primitive.TryGetProperty("extras", out JsonElement extras)
            || extras.ValueKind != JsonValueKind.Object
            || !extras.TryGetProperty("kartzBlocks", out JsonElement runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        List<(int Mode, int TriangleCount)> blocks = [];
        int total = 0;
        int[] flat = [.. runs.EnumerateArray().Select(e => e.TryGetInt32(out int v) ? v : -1)];
        if (flat.Length == 0 || flat.Length % 2 != 0 || Array.IndexOf(flat, -1) >= 0)
        {
            return null;
        }
        for (int i = 0; i < flat.Length; i += 2)
        {
            int mode = flat[i], count = flat[i + 1];
            if (mode is < 0 or > 3 || count <= 0)
            {
                return null;
            }
            blocks.Add((mode, count));
            total += count;
        }
        return total == triangleCount ? blocks : null;
    }
    private static float[] ReadOrDefault(
        IGltfSource source, JsonElement attributes, string name, int components,
        int count, float fallback)
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
        float[] values = source.ReadAccessor(reference.GetInt32(), components);
        if (values.Length == count * components)
        {
            return values;
        }
        float[] padded = new float[count * components];
        values.AsSpan(0, Math.Min(values.Length, padded.Length)).CopyTo(padded);
        return padded;
    }
    private static int[] ReadIndices(IGltfSource source, JsonElement primitive, int count)
    {
        if (!primitive.TryGetProperty("indices", out JsonElement reference))
        {
            return [.. Enumerable.Range(0, count)];
        }
        float[] raw = source.ReadAccessor(reference.GetInt32(), 1);
        int[] indices = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            int index = (int)raw[i];
            if (index < 0 || index >= count)
            {
                throw new InvalidDataException(
                    $"A glTF index is {index}, outside the primitive's {count} vertices.");
            }
            indices[i] = index;
        }
        return indices;
    }
    private static int[] ResolveMaterialTextures(
        IGltfSource source, IReadOnlyList<SskTexture> targetTextures, out List<string> warnings)
    {
        warnings = [];
        IReadOnlyList<GltfMaterial> materials = source.Materials;
        int[] result = new int[materials.Count];
        for (int i = 0; i < materials.Count; i++)
        {
            if (TryParseSlotName(materials[i].Name, out int named))
            {
                result[i] = named;
                continue;
            }
            if (materials[i].Image is not { } image)
            {
                result[i] = Ordinal(i, targetTextures.Count);
                warnings.Add(
                    $"Material {i} ('{materials[i].Name}') has no readable image; " +
                    $"falling back to ordinal texture slot {result[i]}.");
                continue;
            }
            int exact = FindExactMatch(targetTextures, image.Rgba, image.Width, image.Height);
            if (exact >= 0)
            {
                result[i] = exact;
                continue;
            }
            int nearest = FindNearestMatch(targetTextures, image.Rgba, image.Width, image.Height);
            if (nearest >= 0)
            {
                result[i] = nearest;
                warnings.Add(
                    $"Material {i}: no exact texture match ({image.Width}x{image.Height}); " +
                    $"using nearest same-size match '{targetTextures[nearest].Name}'.");
                continue;
            }
            result[i] = Ordinal(i, targetTextures.Count);
            warnings.Add(
                $"Material {i}: no texture match at all; falling back to ordinal slot {result[i]}.");
        }
        return result;
        static int Ordinal(int index, int count)
        {
            return index < count ? index : -1;
        }
    }
    private static bool TryParseSlotName(string name, out int index)
    {
        index = -1;
        const string prefix = "kartz_tex_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(prefix.Length), out int parsed))
        {
            return false;
        }
        if (parsed == -1)
        {
            return true;
        }
        if (parsed is < 0 or > MaxTextureSlot)
        {
            return false;
        }
        index = parsed;
        return true;
    }
    private const int MaxTextureSlot = 4095;
    private static int FindExactMatch(IReadOnlyList<SskTexture> targets, byte[] rgba, int w, int h)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            SskTexture t = targets[i];
            if (t.Width == w && t.Height == h && t.Pixels.AsSpan().SequenceEqual(rgba))
            {
                return i;
            }
        }
        return -1;
    }
    private static int FindNearestMatch(IReadOnlyList<SskTexture> targets, byte[] rgba, int w, int h)
    {
        (long R, long G, long B) = AverageColor(rgba);
        int best = -1;
        long bestDist = long.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            SskTexture t = targets[i];
            if (t.Width != w || t.Height != h)
            {
                continue;
            }
            (long R, long G, long B) tAvg = AverageColor(t.Pixels);
            long dr = tAvg.R - R, dg = tAvg.G - G, db = tAvg.B - B;
            long dist = (dr * dr) + (dg * dg) + (db * db);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }
    private static (long R, long G, long B) AverageColor(byte[] rgba)
    {
        long r = 0, g = 0, b = 0;
        int n = rgba.Length / 4;
        for (int i = 0; i < n; i++)
        {
            r += rgba[i * 4];
            g += rgba[(i * 4) + 1];
            b += rgba[(i * 4) + 2];
        }
        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }
}
