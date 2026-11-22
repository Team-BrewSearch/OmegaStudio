using Omega.Plugins;
namespace Disgaea.Formats;
internal readonly record struct MapFocus(int Tile = -1, int Actor = -1, int Chunk = -1, int Event = -1);
internal static class MapScene
{
    private const float HeightUnit = 12f;
    private const float MarkerSize = 0.3f;
    private static readonly float[] s_shade = [0.78f, 0.62f, 0.86f, 0.70f];
    private static readonly byte[][] s_geo = [[224, 224, 224], [230, 60, 60], [70, 200, 90], [70, 110, 230], [240, 210, 60], [180, 90, 220]];
    private static readonly byte[][] s_alliance = [[70, 140, 255], [230, 60, 60], [240, 140, 50], [160, 160, 160], [90, 200, 120]];
    public static SceneGeometry Build(Mpd map, IReadOnlyList<ImageData?> textures, MapFocus focus)
    {
        Builder builder = new(textures.Count);
        int tileIndex = 0;
        foreach (MpdChunk chunk in map.Chunks)
        {
            (float ox, float oz) = Offset(chunk);
            foreach (byte[] tile in chunk.Tiles)
            {
                AddTile(builder, map, textures, tile, ox, oz, tileIndex == focus.Tile);
                tileIndex++;
            }
        }
        for (int c = 0; c < map.Chunks.Count; c++)
        {
            MpdChunk chunk = map.Chunks[c];
            (float ox, float oz) = Offset(chunk);
            for (int e = 0; e < Mpd.EventCount; e++)
            {
                int at = Mpd.EventsAt + (e * 4);
                if (chunk.Header[at + 2] == 0 && chunk.Header[at + 3] == 0)
                {
                    continue;
                }
                float x = (sbyte)chunk.Header[at] + ox;
                float z = (sbyte)chunk.Header[at + 1] + oz;
                bool picked = focus.Chunk == c && focus.Event == e;
                builder.Marker(x + 0.5f, Ground(map, x, z) + 0.02f, z + 0.5f, 0.42f, 0.01f, picked ? [255, 255, 0] : [255, 90, 200]);
            }
        }
        for (int a = 0; a < map.Actors.Count; a++)
        {
            byte[] actor = map.Actors[a];
            float x = (sbyte)actor[5] + 0.5f;
            float z = (sbyte)actor[6] + 0.5f;
            float y = Ground(map, x - 0.5f, z - 0.5f);
            byte[] color = a == focus.Actor ? [255, 255, 0] : s_alliance[Math.Min(actor[4], s_alliance.Length - 1)];
            builder.Marker(x, y + MarkerSize, z, MarkerSize * 0.6f, MarkerSize, color);
        }
        return builder.Finish(textures);
    }
    public static (float X, float Z) Offset(MpdChunk chunk)
    {
        return (chunk.TileOffsetX, chunk.TileOffsetZ);
    }
    private static float Ground(Mpd map, float x, float z)
    {
        float best = 0;
        foreach (MpdChunk chunk in map.Chunks)
        {
            (float ox, float oz) = Offset(chunk);
            foreach (byte[] tile in chunk.Tiles)
            {
                if (Math.Abs(Mpd.X(tile) + ox - x) < 0.01f && Math.Abs(Mpd.Z(tile) + oz - z) < 0.01f)
                {
                    best = Math.Max(best, Enumerable.Range(0, 4).Max(i => Height(tile[96 + i])));
                }
            }
        }
        return best;
    }
    private static float Height(byte value)
    {
        return -(sbyte)value / HeightUnit;
    }
    private static void AddTile(Builder builder, Mpd map, IReadOnlyList<ImageData?> textures, byte[] tile, float ox, float oz, bool picked)
    {
        float x = Mpd.X(tile) + ox;
        float z = Mpd.Z(tile) + oz;
        (float X, float Y, float Z)[] top =
        [
            (x, Height(tile[96]), z),
            (x + 1, Height(tile[97]), z),
            (x, Height(tile[98]), z + 1),
            (x + 1, Height(tile[99]), z + 1),
        ];
        byte[] tint = picked ? [255, 230, 90] : [255, 255, 255];
        AddFace(builder, map, textures, tile, 4, [top[1], top[0], top[2], top[3]], 1f, tint);
        for (int side = 0; side < 4; side++)
        {
            AddFace(builder, map, textures, tile, side, Side(side, top, tile), s_shade[side], tint);
        }
        if (tile[121] != 0)
        {
            byte[] color = s_geo[Math.Min(tile[120], s_geo.Length - 1)];
            float y = top.Max(c => c.Y) + 0.03f;
            builder.Quad([(x + 0.25f, y, z + 0.25f), (x + 0.75f, y, z + 0.25f), (x + 0.75f, y, z + 0.75f), (x + 0.25f, y, z + 0.75f)], -1, null, color);
        }
    }
    private static (float X, float Y, float Z)[] Side(int side, (float X, float Y, float Z)[] top, byte[] tile)
    {
        float B(int i)
        {
            return Height(tile[100 + (side * 2) + i]);
        }
        return side switch
        {
            0 => [(top[1].X, B(1), top[1].Z), (top[0].X, B(0), top[0].Z), top[0], top[1]],
            1 => [(top[3].X, B(0), top[3].Z), (top[1].X, B(1), top[1].Z), top[1], top[3]],
            2 => [(top[2].X, B(1), top[2].Z), (top[3].X, B(0), top[3].Z), top[3], top[2]],
            _ => [(top[0].X, B(0), top[0].Z), (top[2].X, B(1), top[2].Z), top[2], top[0]],
        };
    }
    private static void AddFace(Builder builder, Mpd map, IReadOnlyList<ImageData?> textures, byte[] tile, int slot, (float X, float Y, float Z)[] corners, float shade, byte[] tint)
    {
        int at = slot * 8;
        int index = tile[at + 6];
        if ((tile[at + 3] == 0 && tile[at + 4] == 0) || index == 0 || index > map.Textures.Count || map.Textures[index - 1] == Mpd.NoTexture)
        {
            return;
        }
        int texture = index - 1;
        byte[] color = [(byte)(tint[0] * shade), (byte)(tint[1] * shade), (byte)(tint[2] * shade)];
        if (texture >= textures.Count || textures[texture] is not { } image)
        {
            builder.Quad(corners, -1, null, color);
            return;
        }
        builder.Quad(corners, texture, FaceUv(tile, slot, image), color);
    }
    public static (float U, float V)[] TopUv(byte[] tile, ImageData image)
    {
        (float U, float V)[] uv = FaceUv(tile, 4, image);
        return [uv[1], uv[0], uv[2], uv[3]];
    }
    private static (float U, float V)[] FaceUv(byte[] tile, int slot, ImageData image)
    {
        int at = slot * 8;
        float u = tile[at];
        float v = tile[at + 1];
        float vRight = tile[at + 2];
        float width = tile[at + 3];
        float height = tile[at + 4];
        float heightRight = tile[at + 5];
        int transform = tile[at + 7];
        (float U, float V)[] uv =
        [
            (u + width, vRight + heightRight),
            (u, v + height),
            (u, v),
            (u + width, vRight),
        ];
        int rotate = transform & 3;
        uv = [.. uv.Skip(rotate), .. uv.Take(rotate)];
        if ((transform & 4) != 0)
        {
            (uv[0], uv[1]) = (uv[1], uv[0]);
            (uv[3], uv[2]) = (uv[2], uv[3]);
        }
        if ((transform & 8) != 0)
        {
            (uv[0], uv[3]) = (uv[3], uv[0]);
            (uv[1], uv[2]) = (uv[2], uv[1]);
        }
        float scaleU = 2f * image.Width;
        float scaleV = 2f * image.Height;
        return [.. uv.Select(p => (p.U / scaleU, p.V / scaleV))];
    }
    private sealed class Builder(int textureCount)
    {
        private readonly List<float> _positions = [];
        private readonly List<float> _normals = [];
        private readonly List<float> _uvs = [];
        private readonly List<byte> _colors = [];
        private readonly List<int>[] _indices = [.. Enumerable.Range(0, textureCount + 1).Select(_ => new List<int>())];
        public void Quad((float X, float Y, float Z)[] corners, int texture, (float U, float V)[]? uv, byte[] color)
        {
            int first = _positions.Count / 3;
            (float X, float Y, float Z)[] mirrored = [.. corners.Select(c => (c.X, c.Y, -c.Z))];
            (float nx, float ny, float nz) = Normal(mirrored);
            for (int i = 0; i < 4; i++)
            {
                _positions.Add(mirrored[i].X);
                _positions.Add(mirrored[i].Y);
                _positions.Add(mirrored[i].Z);
                _normals.Add(nx);
                _normals.Add(ny);
                _normals.Add(nz);
                _uvs.Add(uv?[i].U ?? 0);
                _uvs.Add(uv?[i].V ?? 0);
                _colors.Add(color[0]);
                _colors.Add(color[1]);
                _colors.Add(color[2]);
                _colors.Add(255);
            }
            _indices[texture + 1].AddRange([first, first + 3, first + 1, first + 1, first + 3, first + 2]);
        }
        public void Marker(float x, float y, float z, float half, float height, byte[] color)
        {
            (float, float, float)[] bottom = [(x - half, y, z - half), (x + half, y, z - half), (x + half, y, z + half), (x - half, y, z + half)];
            (float, float, float)[] top = [.. bottom.Select(p => (p.Item1, p.Item2 + height, p.Item3))];
            Quad(top, -1, null, color);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                Quad([bottom[i], bottom[j], top[j], top[i]], -1, null, [.. color.Select(c => (byte)(c * 0.75f))]);
            }
        }
        public SceneGeometry Finish(IReadOnlyList<ImageData?> textures)
        {
            List<int> indices = [];
            List<SceneSubmesh> submeshes = [];
            List<ImageData> images = [];
            for (int t = 0; t < _indices.Length; t++)
            {
                if (_indices[t].Count == 0)
                {
                    continue;
                }
                int texture = -1;
                if (t > 0 && textures[t - 1] is { } image)
                {
                    texture = images.Count;
                    images.Add(image);
                }
                submeshes.Add(new SceneSubmesh(t == 0 ? "markers" : TextureName(t), indices.Count, _indices[t].Count, texture, t > 0));
                indices.AddRange(_indices[t]);
            }
            SceneMesh mesh = new()
            {
                Name = "map",
                Positions = [.. _positions],
                Normals = [.. _normals],
                Uvs = [.. _uvs],
                Colors = [.. _colors],
                Indices = [.. indices],
                Submeshes = submeshes,
            };
            return new SceneGeometry
            {
                Meshes = [mesh],
                Textures = images,
                Lighting = SceneLighting.VertexBaked,
                Filtering = TextureFiltering.Nearest,
            };
            string TextureName(int t)
            {
                return textures[t - 1]?.Name ?? $"texture {t - 1}";
            }
        }
        private static (float, float, float) Normal((float X, float Y, float Z)[] c)
        {
            (float ax, float ay, float az) = (c[1].X - c[0].X, c[1].Y - c[0].Y, c[1].Z - c[0].Z);
            (float bx, float by, float bz) = (c[3].X - c[0].X, c[3].Y - c[0].Y, c[3].Z - c[0].Z);
            (float nx, float ny, float nz) = ((ay * bz) - (az * by), (az * bx) - (ax * bz), (ax * by) - (ay * bx));
            float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            return length < 1e-6f ? (0, 1, 0) : (nx / length, ny / length, nz / length);
        }
    }
}
