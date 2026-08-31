using Omega.Plugins;
using System.Text.Json.Nodes;

namespace Kartz.Formats.Scene;

/// <summary>
/// Turns a decoded <see cref="SskTrack"/> into the neutral geometry the host draws
/// and exports.
///
/// <para>
/// Unlike a character model, a track cannot be handed over as one mesh. The display
/// list is divided into spatial chunks and the draw sections cut across all of them,
/// so the geometry is re-bucketed here by <c>(chunk, texture)</c> — one
/// <see cref="SceneMesh"/> per bucket, named <c>chunk_&lt;index&gt;</c>. That naming
/// is not cosmetic: it is how a re-imported glTF says which chunk its triangles came
/// from, which is what makes an unmodified round trip byte-identical.
/// </para>
/// </summary>
internal static class TrackScene
{
    public static SceneGeometry Build(SskTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        List<SceneMesh> meshes = [];

        foreach ((int chunk, int texture, List<int> triangles) in Bucket(track))
        {
            meshes.Add(BuildMesh(track, chunk, texture, triangles));
        }

        return new SceneGeometry
        {
            Meshes = meshes,
            Textures = [.. track.Textures.Select(ToImage)],

            // The DS bakes a track's lighting into its vertex colours, so the viewport
            // must add none of its own — shading on top of a lightmap darkens the whole
            // course, worst on the terrain, where surfaces sit at grazing angles.
            Lighting = SceneLighting.VertexBaked,

            // DS texels are hand-placed and there are few of them; interpolating reads
            // as smeared rather than smooth.
            Filtering = TextureFiltering.Nearest,
        };
    }

    /// <summary>
    /// A texture as the host's neutral image.
    ///
    /// <para>
    /// A degenerate texture becomes a single transparent pixel rather than an error.
    /// The archive genuinely contains slots that decode to nothing, and a track's
    /// submeshes address their texture by position — so dropping one would shift every
    /// slot after it and repaint half the course with the wrong image. A placeholder
    /// holds the position and draws nothing.
    /// </para>
    /// </summary>
    public static ImageData ToImage(SskTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return IsUsable(texture)
            ? ImageData.Create(
                texture.Name, texture.Width, texture.Height, texture.Pixels, $"5TX fmt{texture.Format}")
            : ImageData.Create(texture.Name, 1, 1, [0, 0, 0, 0], "5TX (empty)");
    }

    /// <summary>Whether a texture decoded to an image with pixels in it.</summary>
    public static bool IsUsable(SskTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return texture.Width > 0
            && texture.Height > 0
            && texture.Pixels.Length == texture.Width * texture.Height * 4;
    }

    /// <summary>
    /// Groups every triangle by the chunk it belongs to and then by its texture,
    /// preserving the original triangle order within each group.
    ///
    /// <para>
    /// Order matters more than it looks: the block layout recorded alongside each
    /// group is a run-length list in triangle order, so re-ordering here would
    /// silently misalign it against the geometry and produce a valid-looking file
    /// that rebuilds the display list wrongly.
    /// </para>
    /// </summary>
    private static IEnumerable<(int Chunk, int Texture, List<int> Triangles)> Bucket(SskTrack track)
    {
        int chunkCount = System.Math.Max(1, track.ChunkCount);
        List<(int Triangle, int Texture)>[] byChunk = new List<(int, int)>[chunkCount];

        foreach (SskDrawSection section in track.Sections)
        {
            int firstTriangle = section.IndexFirst / 3;
            for (int t = 0; t < section.IndexCount / 3; t++)
            {
                int triangle = firstTriangle + t;
                int chunk = triangle < track.TriangleChunkIndex.Length
                    ? track.TriangleChunkIndex[triangle]
                    : 0;

                if (chunk < 0 || chunk >= chunkCount)
                {
                    continue;
                }

                (byChunk[chunk] ??= []).Add((triangle, section.TextureIndex));
            }
        }

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            if (byChunk[chunk] is not { Count: > 0 } triangles)
            {
                // An empty chunk emits no mesh: its absence from the export is the
                // signal, and inventing an empty one would add a chunk on reimport.
                continue;
            }

            // GroupBy keeps first-occurrence order, and sections are already
            // texture-contiguous, so each texture's triangles are already together.
            foreach (IGrouping<int, (int Triangle, int Texture)> group in
                     triangles.GroupBy(entry => entry.Texture))
            {
                yield return (chunk, group.Key, [.. group.Select(entry => entry.Triangle)]);
            }
        }
    }

    /// <summary>
    /// Builds one bucket's mesh, sharing identical vertices within it.
    ///
    /// <para>
    /// The de-duplication is not a size optimisation. A re-import has only
    /// index-sharing to tell it which triangles were ever adjacent — there is no
    /// other signal that two independently duplicated corners were once one vertex —
    /// so an export that duplicated every corner could never reconstruct any of the
    /// triangle strips the real format uses, however connected the source geometry is.
    /// Equality is bit-pattern, not tolerance: two corners are the same vertex only if
    /// they would encode identically.
    /// </para>
    /// </summary>
    private static SceneMesh BuildMesh(SskTrack track, int chunk, int texture, List<int> triangles)
    {
        (float width, float height) = TextureSize(track, texture);

        List<float> positions = [], normals = [], uvs = [];
        List<byte> colors = [];
        List<int> indices = [];
        Dictionary<VertexKey, int> shared = [];

        foreach (int triangle in triangles)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = track.Indices[(triangle * 3) + corner];

                float px = track.Positions[(vertex * 3) + 0];
                float py = track.Positions[(vertex * 3) + 1];
                float pz = track.Positions[(vertex * 3) + 2];
                float nx = track.Normals[(vertex * 3) + 0];
                float ny = track.Normals[(vertex * 3) + 1];
                float nz = track.Normals[(vertex * 3) + 2];

                // The format stores UVs in texture pixels; everything downstream — the
                // shader and the glTF exporter alike — wants them normalised.
                float u = track.Uvs[(vertex * 2) + 0] / width;
                float v = track.Uvs[(vertex * 2) + 1] / height;

                byte r = track.Colors[(vertex * 4) + 0];
                byte g = track.Colors[(vertex * 4) + 1];
                byte b = track.Colors[(vertex * 4) + 2];
                byte a = track.Colors[(vertex * 4) + 3];

                VertexKey key = VertexKey.Of(px, py, pz, nx, ny, nz, u, v, r, g, b, a);
                if (!shared.TryGetValue(key, out int index))
                {
                    index = positions.Count / 3;
                    shared[key] = index;

                    positions.Add(px);
                    positions.Add(py);
                    positions.Add(pz);
                    normals.Add(nx);
                    normals.Add(ny);
                    normals.Add(nz);
                    uvs.Add(u);
                    uvs.Add(v);
                    colors.Add(r);
                    colors.Add(g);
                    colors.Add(b);
                    colors.Add(a);
                }

                indices.Add(index);
            }
        }

        bool alphaClip = texture >= 0 && texture < track.Textures.Length
            && track.Textures[texture].HasTransparency;

        return new SceneMesh
        {
            // Read back on re-import to recover the chunk. See the class remarks.
            Name = $"chunk_{chunk}",
            Positions = [.. positions],
            Normals = [.. normals],
            Uvs = [.. uvs],
            Colors = [.. colors],
            Indices = [.. indices],
            Submeshes =
            [
                new SceneSubmesh(
                    // The authoritative texture slot, carried in the name because the
                    // display index below cannot always hold it: a track may reference
                    // a slot beyond the textures that actually resolved, and losing the
                    // number would demote those groups to untextured on re-import.
                    $"kartz_tex_{texture}",
                    0,
                    indices.Count,
                    texture >= 0 && texture < track.Textures.Length ? texture : -1,
                    alphaClip)
                {
                    Extras = RoundTripHints(track, chunk, texture, triangles),
                },
            ],
        };
    }

    private static (float Width, float Height) TextureSize(SskTrack track, int texture)
    {
        if (texture < 0 || texture >= track.Textures.Length)
        {
            return (1f, 1f);
        }

        SskTexture found = track.Textures[texture];
        return (System.Math.Max(1, found.Width), System.Math.Max(1, found.Height));
    }

    /// <summary>
    /// The source detail a re-import needs and the geometry cannot carry.
    ///
    /// <para>
    /// Whether a run of triangles shipped as a mode-2 strip or a mode-0 loose batch is
    /// source information no heuristic can recover — real tracks are full of mode-0
    /// blocks whose triangles would strip perfectly well. Preserving the original
    /// layout is what makes an unmodified round trip byte-identical, and glTF's
    /// <c>extras</c> is exactly where the spec says to put it: every other tool
    /// ignores it, and the geometry itself stays plain triangles so the file still
    /// renders anywhere.
    /// </para>
    /// </summary>
    private static Dictionary<string, JsonNode> RoundTripHints(
        SskTrack track, int chunk, int texture, List<int> triangles)
    {
        JsonArray blocks = [];
        JsonArray groupBreaks = [];

        int runMode = -1, runCount = 0;
        int previousBlock = int.MinValue, previousGroup = int.MinValue;

        foreach (int triangle in triangles)
        {
            int block = At(track.TriangleBlockId, triangle, -1);
            int mode = At(track.TriangleBlockMode, triangle, 0);
            int group = At(track.TriangleGroupId, triangle, -1);

            if (block != previousBlock)
            {
                if (runCount > 0)
                {
                    blocks.Add(runMode);
                    blocks.Add(runCount);
                }

                // A texture group can restart mid-mesh even for the same texture, so
                // record which block runs begin one. Block 0 always does, implicitly.
                if (previousGroup != int.MinValue && group != previousGroup)
                {
                    groupBreaks.Add(blocks.Count / 2);
                }

                runMode = mode;
                runCount = 0;
                previousBlock = block;
                previousGroup = group;
            }

            runCount++;
        }

        if (runCount > 0)
        {
            blocks.Add(runMode);
            blocks.Add(runCount);
        }

        Dictionary<string, JsonNode> extras = new(StringComparer.Ordinal)
        {
            ["kartzBlocks"] = blocks,
        };

        if (groupBreaks.Count > 0)
        {
            extras["kartzGroupBreaks"] = groupBreaks;
        }

        // Per-emitted-vertex TEXCOORD flags, as a compact 0/1 string in emission
        // order. Advisory only: the writer emits a TEXCOORD whenever the value
        // actually changed, so a stale flag can cost a redundant opcode and never a
        // wrong UV.
        if (track.VertexTexcoordEmitted.TryGetValue((chunk, texture), out bool[]? emitted))
        {
            extras["kartzTexcoordEmit"] = JsonValue.Create(
                string.Concat(emitted.Select(flag => flag ? '1' : '0')));
        }

        return extras;
    }

    private static int At(int[] values, int index, int fallback)
    {
        return index >= 0 && index < values.Length ? values[index] : fallback;
    }

    /// <summary>
    /// A vertex compared by the exact bits of every attribute.
    ///
    /// <para>
    /// The floats are held as their bit patterns rather than as floats, because
    /// <see cref="float"/> equality is not bit equality: it calls -0.0 equal to 0.0
    /// and NaN unequal to itself, either of which would merge or split vertices that
    /// the encoder would not. See <see cref="BuildMesh"/> for why this has to be an
    /// exact comparison and not a tolerance.
    /// </para>
    /// </summary>
    private readonly record struct VertexKey(
        int X, int Y, int Z,
        int Nx, int Ny, int Nz,
        int U, int V,
        int Color)
    {
        public static VertexKey Of(
            float x, float y, float z,
            float nx, float ny, float nz,
            float u, float v,
            byte r, byte g, byte b, byte a)
        {
            return new VertexKey(
                BitConverter.SingleToInt32Bits(x),
                BitConverter.SingleToInt32Bits(y),
                BitConverter.SingleToInt32Bits(z),
                BitConverter.SingleToInt32Bits(nx),
                BitConverter.SingleToInt32Bits(ny),
                BitConverter.SingleToInt32Bits(nz),
                BitConverter.SingleToInt32Bits(u),
                BitConverter.SingleToInt32Bits(v),
                (r << 24) | (g << 16) | (b << 8) | a);
        }
    }
}
