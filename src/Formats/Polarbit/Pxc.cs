using System.Text;
using Omega.Plugins;
namespace Polarbit.Formats;

/// <summary>One vertex block of a PXC mesh: fixed position and UV, optionally a normal (0x10) and an RGBA colour (0x100).</summary>
internal readonly record struct PxcVertexBlock(int Offset, int Format, int Count)
{
    public int Stride => 20 + ((Format & 0x10) != 0 ? 12 : 0) + ((Format & 0x100) != 0 ? 4 : 0);
    public bool HasNormal => (Format & 0x10) != 0;
    public bool HasColour => (Format & 0x100) != 0;
    public int ColourAt => 12 + (HasNormal ? 12 : 0);
}

/// <summary>A draw range: material, which index and vertex block, and the run of indices.</summary>
internal readonly record struct PxcDraw(int Material, int IndexBlock, int VertexBlock, int First, int Count);

/// <summary>A MESH or LICM chunk inside an SGPS object (REPORT 9.5).</summary>
internal sealed class PxcMesh
{
    public required string Chunk { get; init; }
    public required int ChunkOffset { get; init; }
    public required int BoundsOffset { get; init; }
    public List<PxcVertexBlock> VertexBlocks { get; } = [];
    public List<int[]> IndexBlocks { get; } = [];
    public List<string?> Textures { get; } = [];
    public List<PxcDraw> Draws { get; } = [];
    public int MaterialsOffset { get; set; }
    public int RangesOffset { get; set; }
}

/// <summary>One object of the scene graph, with its placement relative to its parent.</summary>
internal sealed class PxcObject
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public required int Parent { get; init; }
    public float[]? Matrix { get; set; }
    public float[]? Translation { get; set; }
    public int PlacementOffset { get; set; } = -1;
    public PxcMesh? Mesh { get; set; }
    /// <summary>Row-major 3x3 world matrix and translation: world = M * v + t.</summary>
    public float[] WorldMatrix { get; set; } = [1, 0, 0, 0, 1, 0, 0, 0, 1];
    public float[] WorldTranslation { get; set; } = [0, 0, 0];
    public string Label => $"{(Name.Length > 0 ? Name : Tag)}_{Index}";
    public int Index { get; init; }
}

/// <summary>PXC scene graph: a tree of typed objects, used for cars, tracks and props in the 2011 games (REPORT 9).</summary>
internal sealed class PxcScene
{
    private static readonly string[] s_rgContainers = ["SGGR", "SGNO", "SGAN", "SGG2"];
    private static readonly string[] s_rtContainers = ["SGGR", "SGNO", "SGPC"];
    private static readonly string[] s_rgPlaced = ["SGNO", "SGPS", "SGSP", "SGCA", "SGPR", "SGLI", "SGMR", "SGAN"];
    private static readonly string[] s_rtPlaced = ["SGNO", "SGPS", "SGSP", "SGCA", "SGME"];
    private PxcScene()
    {
    }
    public int Version { get; private init; }
    public bool Getaway => Version >= 50;
    public List<PxcObject> Objects { get; } = [];

    public static PxcScene Parse(byte[] b)
    {
        Le.Need(b.Length >= 8 && Le.U32(b, 0) == 0x1A2B3C4D && Le.U16(b, 6) == 1, "PXC header is missing.");
        int version = Le.U16(b, 4);
        Le.Need(version is 16 or 19 or 20 or 54 or 55, $"PXC version {version} is not supported.");
        PxcScene scene = new() { Version = version };
        Le.Need(scene.ReadObject(b, 8, -1) == b.Length, "PXC has trailing bytes after its root object.");
        foreach (PxcObject o in scene.Objects)
        {
            float[] m = o.Matrix ?? [1, 0, 0, 0, 1, 0, 0, 0, 1], t = o.Translation ?? [0, 0, 0];
            if (o.Parent >= 0)
            {
                PxcObject p = scene.Objects[o.Parent];
                float[] pm = p.WorldMatrix, pt = p.WorldTranslation;
                float[] wm = new float[9], wt = new float[3];
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        wm[(3 * r) + c] = (pm[3 * r] * m[c]) + (pm[(3 * r) + 1] * m[3 + c]) + (pm[(3 * r) + 2] * m[6 + c]);
                    }
                    wt[r] = (pm[3 * r] * t[0]) + (pm[(3 * r) + 1] * t[1]) + (pm[(3 * r) + 2] * t[2]) + pt[r];
                }
                (m, t) = (wm, wt);
            }
            o.WorldMatrix = m;
            o.WorldTranslation = t;
        }
        return scene;
    }

    private static (string Text, int Next) String(byte[] b, int p)
    {
        int n = Le.U16(b, p);
        Le.Need(n > 0 && p + 2 + n <= b.Length && b[p + 1 + n] == 0, $"PXC string at 0x{p:X} is not terminated.");
        return (Encoding.Latin1.GetString(b, p + 2, n - 1), p + 2 + n);
    }

    private static (string Tag, int Data, int End) Chunk(byte[] b, int p)
    {
        Le.Need(p + 8 <= b.Length, $"PXC chunk at 0x{p:X} is truncated.");
        long end = p + 8L + Le.U32(b, p + 4);
        Le.Need(end <= b.Length, $"PXC chunk at 0x{p:X} runs past the file.");
        return (Encoding.Latin1.GetString(b, p, 4), p + 8, (int)end);
    }

    private static int SkipProperties(byte[] b, int p)
    {
        int count = Le.Count(b, p, 10, "PXC property list");
        p += 4;
        for (int i = 0; i < count; i++)
        {
            p = String(b, p).Next;
            p = Chunk(b, p).End;
        }
        return p;
    }

    private int ReadObject(byte[] b, int p, int parent)
    {
        (string tag, int body, int end) = Chunk(b, p);
        (string name, int q) = String(b, body);
        q += 4;
        PxcObject o = new() { Tag = tag, Name = name, Offset = p, Parent = parent, Index = Objects.Count };
        Objects.Add(o);
        if (Getaway)
        {
            q = SkipProperties(b, q);
        }
        if ((Getaway ? s_rgContainers : s_rtContainers).Contains(tag))
        {
            if (Getaway)
            {
                Le.Need(Le.I32(b, q) == -1, $"PXC container '{name}' lacks its -1 marker.");
                q += 4;
            }
            int children = Le.Count(b, q, 8, "PXC child list");
            q += 4;
            for (int i = 0; i < children; i++)
            {
                q = ReadObject(b, q, o.Index);
            }
        }
        if ((Getaway ? s_rgPlaced : s_rtPlaced).Contains(tag))
        {
            Le.Need(q + 76 <= end, $"PXC object '{name}' is too short for its placement.");
            o.PlacementOffset = q;
            o.Matrix = [.. Enumerable.Range(0, 9).Select(i => Le.Fx(b, q + 28 + (4 * i)))];
            o.Translation = [.. Enumerable.Range(0, 3).Select(i => Le.Fx(b, q + 64 + (4 * i)))];
            q += 76 + (Getaway ? 1 : 0);
            q = Chunk(b, q).End;
        }
        if (tag == "SGPS")
        {
            (string chunk, int data, int chunkEnd) = Chunk(b, q);
            Le.Need(chunk is "MESH" or "LICM", $"PXC mesh object '{name}' holds a '{chunk}' chunk.");
            o.Mesh = ReadMesh(b, chunk, q, data, chunkEnd);
        }
        return end;
    }

    private PxcMesh ReadMesh(byte[] b, string chunk, int offset, int d, int e)
    {
        PxcMesh mesh = new() { Chunk = chunk, ChunkOffset = offset, BoundsOffset = d };
        int vertexBlocks = 1, indexBlocks = 1, q = d + 28;
        if (Getaway)
        {
            vertexBlocks = (int)Le.U32(b, d + 28);
            indexBlocks = (int)Le.U32(b, d + 32);
            q = d + 36;
            Le.Need(vertexBlocks < 256 && indexBlocks < 256, "PXC mesh block counts are out of range.");
        }
        for (int i = 0; i < vertexBlocks; i++)
        {
            int format = (int)Le.U32(b, q), count = (int)Le.U32(b, q + 4);
            Le.Need(format is 0x1000 or 0x1010 or 0x1100 or 0x1110 && Le.U32(b, q + 8) == 0, $"PXC vertex format 0x{format:X} is not supported.");
            PxcVertexBlock block = new(q + 12, format, count);
            Le.Need(block.Offset + ((long)count * block.Stride) <= e, "PXC vertex block runs past its mesh.");
            mesh.VertexBlocks.Add(block);
            q = block.Offset + (count * block.Stride);
        }
        for (int i = 0; i < indexBlocks; i++)
        {
            int count = (int)Le.U32(b, q + 4);
            Le.Need(Le.U32(b, q) == 4 && Le.U32(b, q + 8) == 5 && q + 12 + (2L * count) <= e, "PXC index block is not a GL_TRIANGLES ushort list.");
            mesh.IndexBlocks.Add([.. Enumerable.Range(0, count).Select(k => (int)Le.U16(b, q + 12 + (2 * k)))]);
            q += 12 + (2 * count);
        }
        mesh.MaterialsOffset = q;
        if (Getaway)
        {
            (string tag, int m, int garmEnd) = Chunk(b, q);
            Le.Need(tag == "GaRM" && Le.U32(b, m + 4) == 76, "PXC mesh lacks its GaRM material block.");
            int count = (int)Le.U32(b, m);
            m += 8;
            for (int i = 0; i < count; i++)
            {
                (m, string? texture) = Texture(b, m);
                mesh.Textures.Add(texture);
                m += 64;
            }
            Le.Need(m == garmEnd, "PXC GaRM size does not match its materials.");
            q = garmEnd;
        }
        else
        {
            int count = (int)Le.U32(b, q);
            q += 4;
            for (int i = 0; i < count; i++)
            {
                (q, string? texture) = Texture(b, q);
                mesh.Textures.Add(texture);
                q += (Version == 16 ? 36 : 44) + 4;
            }
        }
        mesh.RangesOffset = q;
        int ranges = (int)Le.U32(b, q), rangeSize = Getaway ? 16 : 8;
        q += 4;
        Le.Need(q + ((long)ranges * rangeSize) <= e, "PXC draw ranges run past the mesh.");
        for (int i = 0; i < ranges; i++)
        {
            int at = q + (rangeSize * i);
            mesh.Draws.Add(new PxcDraw(Le.U16(b, at + 2), 0, 0, Le.U16(b, at + 6), Le.U16(b, at + 4)));
        }
        q += ranges * rangeSize;
        if (chunk == "LICM" && Getaway)
        {
            int cells = (int)Le.U32(b, q + 4), count = (int)Le.U32(b, q + 8);
            q += 12 + (4 * cells);
            Le.Need(q + (16L * count) <= e, "PXC LICM ranges run past the mesh.");
            for (int i = 0; i < count; i++)
            {
                int at = q + (16 * i);
                mesh.Draws.Add(new PxcDraw(Le.U16(b, at + 6), Le.U16(b, at + 10), Le.U16(b, at + 8), Le.U16(b, at + 2), Le.U16(b, at + 4)));
            }
        }
        foreach (PxcDraw draw in mesh.Draws)
        {
            Le.Need(draw.Material < mesh.Textures.Count && draw.VertexBlock < mesh.VertexBlocks.Count && draw.IndexBlock < mesh.IndexBlocks.Count
                && draw.First + draw.Count <= mesh.IndexBlocks[draw.IndexBlock].Length, "PXC draw range points outside its mesh.");
        }
        return mesh;
    }

    /// <summary>TEXT (flags + path), or an empty slot: #RES + 0 in RT2, eight zero bytes in Getaway.</summary>
    private static (int Next, string? Texture) Texture(byte[] b, int p)
    {
        (string tag, int data, int end) = Chunk(b, p);
        return tag == "TEXT" ? (end, String(b, data + 4).Text.Replace('\\', '/')) : (end, null);
    }

    public static (float X, float Y, float Z) Apply(float[] m, float[] t, float x, float y, float z)
    {
        return ((m[0] * x) + (m[1] * y) + (m[2] * z) + t[0], (m[3] * x) + (m[4] * y) + (m[5] * z) + t[1], (m[6] * x) + (m[7] * y) + (m[8] * z) + t[2]);
    }

    public static float[] Inverse(float[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        Le.Need(System.Math.Abs(det) > 1e-12, "PXC object has a singular placement.");
        return [.. new[] { (e * i) - (f * h), (c * h) - (b * i), (b * f) - (c * e), (f * g) - (d * i), (a * i) - (c * g), (c * d) - (a * f), (d * h) - (e * g), (b * g) - (a * h), (a * e) - (b * d) }.Select(x => (float)(x / det))];
    }

    public SceneGeometry BuildScene(byte[] b, TextureSet textures)
    {
        List<SceneMesh> meshes = [];
        bool normals = false, colours = false;
        foreach (PxcObject o in Objects)
        {
            if (o.Mesh is not { } mesh)
            {
                continue;
            }
            float[] m = o.WorldMatrix, t = o.WorldTranslation;
            float[] inverse = Inverse(m);
            float[] normalMatrix = [inverse[0], inverse[3], inverse[6], inverse[1], inverse[4], inverse[7], inverse[2], inverse[5], inverse[8]];
            for (int v = 0; v < mesh.VertexBlocks.Count; v++)
            {
                PxcVertexBlock block = mesh.VertexBlocks[v];
                List<int> indices = [];
                List<SceneSubmesh> submeshes = [];
                foreach (PxcDraw draw in mesh.Draws.Where(x => x.VertexBlock == v))
                {
                    int first = indices.Count;
                    int[] source = mesh.IndexBlocks[draw.IndexBlock];
                    for (int k = draw.First; k + 2 < draw.First + draw.Count; k += 3)
                    {
                        Le.Need(source[k] < block.Count && source[k + 1] < block.Count && source[k + 2] < block.Count, $"PXC mesh '{o.Label}' indexes past its vertices.");
                        indices.AddRange([source[k], source[k + 1], source[k + 2]]);
                    }
                    string? texture = mesh.Textures[draw.Material];
                    submeshes.Add(new SceneSubmesh(texture is null ? "untextured" : Path.GetFileNameWithoutExtension(texture), first, indices.Count - first, textures.Add(texture)));
                }
                if (indices.Count == 0)
                {
                    continue;
                }
                float[] positions = new float[block.Count * 3], uvs = new float[block.Count * 2];
                float[] normal = block.HasNormal ? new float[block.Count * 3] : [];
                byte[] colour = block.HasColour ? new byte[block.Count * 4] : [];
                for (int k = 0; k < block.Count; k++)
                {
                    int at = block.Offset + (k * block.Stride);
                    (float x, float y, float z) = Apply(m, t, Le.Fx(b, at), Le.Fx(b, at + 4), Le.Fx(b, at + 8));
                    (positions[k * 3], positions[(k * 3) + 1], positions[(k * 3) + 2]) = Axes.ToHost(x, y, z);
                    uvs[k * 2] = Le.Fx(b, at + block.Stride - 8);
                    uvs[(k * 2) + 1] = Axes.FlipV(Le.Fx(b, at + block.Stride - 4));
                    if (block.HasNormal)
                    {
                        (float nx, float ny, float nz) = Apply(normalMatrix, [0, 0, 0], Le.Fx(b, at + 12), Le.Fx(b, at + 16), Le.Fx(b, at + 20));
                        float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                        length = length > 0 ? length : 1;
                        (normal[k * 3], normal[(k * 3) + 1], normal[(k * 3) + 2]) = Axes.ToHost(nx / length, ny / length, nz / length);
                    }
                    if (block.HasColour)
                    {
                        b.AsSpan(at + block.ColourAt, 4).CopyTo(colour.AsSpan(k * 4));
                    }
                }
                normals |= block.HasNormal;
                colours |= block.HasColour;
                meshes.Add(new SceneMesh
                {
                    Name = mesh.VertexBlocks.Count > 1 ? $"{o.Label}.{v}" : o.Label,
                    Positions = positions,
                    Normals = normal,
                    Uvs = uvs,
                    Colors = colour,
                    Indices = [.. indices],
                    Submeshes = submeshes,
                });
            }
        }
        return new SceneGeometry
        {
            Meshes = meshes,
            Textures = textures.Images,
            Lighting = colours && !normals ? SceneLighting.VertexBaked : SceneLighting.Shaded,
        };
    }
}
