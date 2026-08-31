using Omega.Plugins;
using Omega.Plugins.Gltf;
using System.Numerics;
using System.Text.Json;

namespace Kartz.Formats.Gltf;

/// <summary>
/// Reads a glTF/GLB's mesh geometry for re-import into an existing <see cref="SskTrack"/>.
///
/// Produces <see cref="TrkImportPrimitive"/> — its own independent (non-shared) vertex block
/// per glTF primitive, mirroring the host exporter's one-primitive-per-<see cref="SskDrawSection"/> convention, which
/// is the opposite of the merging <see cref="IGltfCodec.Read"/> does (that merging exists for the
/// skinned-character path this has nothing to do with).
///
/// <para>
/// The track keeps its existing texture slot table — this only replaces geometry — so each
/// primitive's material is resolved back to one of the target's <see cref="SskTexture"/>
/// slots. The host exporter puts the authoritative slot directly in each material's
/// <c>name</c> (<c>"kartz_tex_&lt;index&gt;"</c>) specifically so this doesn't have to guess —
/// pixel-matching the embedded image alone isn't reliable enough on its own: a PNG round-trip
/// of a magenta-colorkeyed texture (see <see cref="SskTexture"/>) can legitimately re-encode different
/// RGB bytes behind fully-transparent pixels (alpha is what matters, not the color hidden under
/// it), which was confirmed to make ~26% of an entirely unmodified FarFarAway round-trip
/// resolve to the wrong texture slot when pixel comparison was the only signal. The name is
/// tried first; decoding the embedded image and matching pixels/size remains as a fallback for
/// glTF files not authored by this app, or where a DCC tool stripped/renamed materials.
/// </para>
/// </summary>
public static class TrkGltfReader
{
    /// <param name="source">
    /// The file, opened by the host at the container level. Read through
    /// <see cref="IGltfCodec.Open"/> rather than <see cref="IGltfCodec.Read"/> because
    /// the neutral document deliberately merges shared vertex blocks and drops
    /// <c>extras</c>, and both are exactly what a byte-exact reconversion needs.
    /// </param>
    /// <param name="targetTextures">The track's existing texture slots, which are kept.</param>
    /// <param name="log">Where non-fatal texture-resolution fallbacks are reported.</param>
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

    /// <summary>
    /// Parses a <c>"chunk_&lt;index&gt;"</c> name (node or mesh) written by the app's own
    /// per-chunk glTF export — see <see cref="TrkImportPrimitive.ChunkIndex"/>.
    /// </summary>
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

            // Invert GltfWriter's uv/texDim normalization back to pixel space.
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

    /// <summary>
    /// Reads <c>extras.kartzGroupBreaks</c> — see <see cref="TrkImportPrimitive.GroupBreaks"/>.
    /// Returns <see langword="null"/> if absent or malformed.
    /// </summary>
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

    /// <summary>
    /// Reads the <c>extras.kartzTexcoordEmit</c> '0'/'1' string written by this app's own glTF
    /// export — see <see cref="TrkImportPrimitive.TexcoordEmit"/>. Returns <see langword="null"/>
    /// if absent or containing anything other than '0'/'1'.
    /// </summary>
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

    /// <summary>
    /// Reads the <c>extras.kartzBlocks</c> flat <c>[mode, triangleCount, …]</c> run list written
    /// by this app's own glTF export — see <see cref="TrkImportPrimitive.Blocks"/>. Returns
    /// <see langword="null"/> if absent or malformed, or if the runs don't account for exactly
    /// <paramref name="triangleCount"/> triangles (which would mean the geometry was edited after
    /// export, making the stale layout worse than no layout at all).
    /// </summary>
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
            // A primitive's indices are local to its own vertex block, so anything outside it
            // is malformed. Caught here because the chunker dereferences these directly.
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

    // ── Texture-index resolution ────────────────────────────────────────────

    /// <summary>
    /// Maps each glTF material to a slot in the target track's existing texture table.
    /// </summary>
    /// <remarks>
    /// Tries, in order: the authoritative <c>kartz_tex_&lt;index&gt;</c> material name this
    /// plugin's own export embeds (see the type-level remarks for why pixel-matching alone
    /// isn't reliable enough to be the primary path); exact pixel match against the decoded
    /// embedded image; nearest same-dimension average-color match; then falls back to the
    /// material's ordinal position, matching the host exporter's one-material-per-section
    /// emission order. Every path below the name match is reported through
    /// <paramref name="warnings"/> — that's the signal something (a non-Kartz-authored file, or
    /// a DCC tool renaming materials) isn't preserving the authoritative reference.
    /// </remarks>
    private static int[] ResolveMaterialTextures(
        IGltfSource source, IReadOnlyList<SskTexture> targetTextures, out List<string> warnings)
    {
        warnings = [];

        // The host decodes every material's base-colour image on the way in, so the
        // PNG plumbing that used to live here — buffer views, data URIs, an image
        // codec — is gone. This is left with the part that is actually about Kartz.
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

    /// <summary>
    /// Parses the <c>"kartz_tex_&lt;index&gt;"</c> material name this plugin's export writes,
    /// if present and valid. <c>-1</c> is a valid, authoritative result (an untextured
    /// section); only a missing or unparseable name returns <see langword="false"/>, so the
    /// caller falls back to pixel matching.
    /// </summary>
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
            return true; // authoritative "no texture" — index already defaulted to -1
        }

        if (parsed is < 0 or > MaxTextureSlot)
        {
            return false;
        }

        // Deliberately NOT bounded by the target's texture count. A slot number is file data —
        // it is what gets written back as the chunk's texture_index — whereas that count is only
        // how many textures this plugin managed to *resolve* for display, and the two legitimately
        // disagree. Madagascar Kartz's ShippingFreighter references slot 34 while its texture
        // directory holds 33 files, so bounding this here silently demoted those groups to
        // untextured on reimport: geometry and UVs survived intact, but texCount went 1 -> 0 and
        // TEXIMAGE_PARAM 0x40000000 -> 0, corrupting two chunks of an otherwise byte-perfect
        // round trip. Callers that *display* a texture still bounds-check separately, so an
        // unresolvable slot renders untextured without losing the number.
        index = parsed;
        return true;
    }

    /// <summary>
    /// Sanity ceiling for a slot number parsed out of a material name — high enough that no real
    /// track approaches it (the largest observed is 62), low enough that a malformed name can't
    /// produce an absurd <c>texture_index</c>.
    /// </summary>
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
