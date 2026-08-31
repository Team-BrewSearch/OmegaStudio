using Omega.Plugins;
using System.Text.Json.Nodes;
namespace Kartz.Formats.Scene;

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
            Lighting = SceneLighting.VertexBaked,
            Filtering = TextureFiltering.Nearest,
        };
    }
    public static ImageData ToImage(SskTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return IsUsable(texture)
            ? ImageData.Create(
                texture.Name, texture.Width, texture.Height, texture.Pixels, $"5TX fmt{texture.Format}")
            : ImageData.Create(texture.Name, 1, 1, [0, 0, 0, 0], "5TX (empty)");
    }
    public static bool IsUsable(SskTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return texture.Width > 0
            && texture.Height > 0
            && texture.Pixels.Length == texture.Width * texture.Height * 4;
    }
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
                continue;
            }
            foreach (IGrouping<int, (int Triangle, int Texture)> group in
                     triangles.GroupBy(entry => entry.Texture))
            {
                yield return (chunk, group.Key, [.. group.Select(entry => entry.Triangle)]);
            }
        }
    }
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
            Name = $"chunk_{chunk}",
            Positions = [.. positions],
            Normals = [.. normals],
            Uvs = [.. uvs],
            Colors = [.. colors],
            Indices = [.. indices],
            Submeshes =
            [
                new SceneSubmesh(
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
