using System.Text;
using Omega.Plugins;
namespace Polarbit.Formats;
internal readonly record struct PiffChunk(string Id, int Header, int Data, int Size, int Depth, int Parent);
internal static class Piff
{
    public static string FileType(ReadOnlySpan<byte> b)
    {
        return Encoding.Latin1.GetString(b.Slice(8, 4));
    }
    public static List<PiffChunk> Walk(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 12 && b.StartsWith("PIFF"u8) && Le.U32(b, 4) == b.Length - 8, "PIFF header does not match the file size.");
        List<PiffChunk> chunks = [];
        Walk(b, 12, b.Length, 0, -1, chunks);
        return chunks;
    }
    private static void Walk(ReadOnlySpan<byte> b, int p, int end, int depth, int parent, List<PiffChunk> chunks)
    {
        while (p < end)
        {
            Le.Need(p + 8 <= end, $"PIFF chunk header at 0x{p:X} runs past its parent.");
            string id = Encoding.Latin1.GetString(b.Slice(p, 4));
            long size = Le.U32(b, p + 4);
            Le.Need(p + 8 + size <= end, $"PIFF chunk '{id}' at 0x{p:X} runs past its parent.");
            if (id == "FORM")
            {
                Le.Need(size >= 4, $"PIFF FORM at 0x{p:X} has no type.");
                chunks.Add(new PiffChunk("FORM:" + Encoding.Latin1.GetString(b.Slice(p + 8, 4)), p, p + 12, (int)size - 4, depth, parent));
                Walk(b, p + 12, p + 8 + (int)size, depth + 1, chunks.Count - 1, chunks);
            }
            else
            {
                chunks.Add(new PiffChunk(id, p, p + 8, (int)size, depth, parent));
            }
            p += 8 + (int)size;
        }
        Le.Need(p == end, "PIFF chunk list is misaligned.");
    }
    public static IEnumerable<PiffChunk> Children(List<PiffChunk> chunks, int parent, string id)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Parent == parent && chunks[i].Id == id)
            {
                yield return chunks[i];
            }
        }
    }
    public static PiffChunk? Child(List<PiffChunk> chunks, int parent, string id)
    {
        foreach (PiffChunk chunk in Children(chunks, parent, id))
        {
            return chunk;
        }
        return null;
    }
    public static int IndexOf(List<PiffChunk> chunks, int parent, string id)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Parent == parent && chunks[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }
}
internal sealed class TextureSet(Func<string, ImageData?> resolve)
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    public List<ImageData> Images { get; } = [];
    public int Add(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return -1;
        }
        if (!_indices.TryGetValue(name, out int index))
        {
            ImageData? image = resolve(name);
            index = image is null ? -1 : Images.Count;
            if (image is not null)
            {
                Images.Add(image);
            }
            _indices[name] = index;
        }
        return index;
    }
}
internal sealed record PiffBone(uint Id, uint Parent, int Rigid, int Weighted, int RigidStart, int WeightedStart, int WeightStart, float[] Pivot)
{
    public string Name => Id < 0x01000000 ? $"bone{Id}"
        : BitConverter.GetBytes(Id) is { } c && c.All(x => x is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(c).Trim() : $"bone{Id:X8}";
}
internal sealed class PiffModel
{
    private PiffModel()
    {
    }
    public int[] Head { get; private init; } = [];
    public List<string> Textures { get; } = [];
    public List<int> Appearances { get; } = [];
    public float[] Positions { get; private set; } = [];
    public float[] Normals { get; private set; } = [];
    public float[] Uvs { get; private set; } = [];
    public byte[] Colors { get; private set; } = [];
    public int ColorBytes { get; private init; }
    public List<(int Mode, int Appearance, int[] Indices)> Polys { get; } = [];
    public List<PiffBone> Bones { get; } = [];
    public List<(int Bone, float Weight)>[] Influences { get; private set; } = [];
    public int[] KeyTimes { get; private set; } = [];
    public List<(string Name, int First, int Last)> Clips { get; } = [];
    public float[][]? Translations { get; private set; }
    public float[][]? Rotations { get; private set; }
    public float[][]? Scales { get; private set; }
    public int LodLevels { get; private set; }
    public string Id { get; private set; } = string.Empty;
    public int VertexCount => Positions.Length / 3;
    public bool IsSkinned => Bones.Count > 0;
    public bool IsAnimated => KeyTimes.Length > 0 && Clips.Count > 0 && ((IsSkinned && Rotations is not null) || Scales is not null);
    public static PiffModel Parse(byte[] b)
    {
        List<PiffChunk> chunks = Piff.Walk(b);
        Le.Need(Piff.FileType(b) == "3DOB", "PIFF file is not a 3DOB model.");
        PiffChunk head = Piff.Child(chunks, -1, "HEAD") ?? throw new InvalidDataException("3DOB has no HEAD chunk.");
        Le.Need(head.Size is 72 or 76, $"3DOB HEAD is {head.Size} bytes, not 72 or 76.");
        int[] h = new int[head.Size / 4];
        for (int i = 0; i < h.Length; i++)
        {
            h[i] = Le.I32(b, head.Data + (4 * i));
        }
        PiffModel model = new() { Head = h, ColorBytes = h[11] == 5121 ? 4 : 3 };
        model.ReadTextures(b, chunks);
        model.ReadAppearances(b, chunks);
        PiffChunk mesh = Piff.Child(chunks, -1, "MESH") ?? throw new InvalidDataException("3DOB has no MESH chunk.");
        int count = mesh.Size / 12;
        model.Positions = Vectors(b, mesh, count, 3);
        if (Piff.Child(chunks, -1, "NORM") is { } norm)
        {
            Le.Need(norm.Size == count * 12, "3DOB NORM does not match the vertex count.");
            model.Normals = Vectors(b, norm, count, 3);
        }
        if (Piff.Child(chunks, -1, "TXUV") is { } txuv)
        {
            Le.Need(txuv.Size == count * 8, "3DOB TXUV does not match the vertex count.");
            model.Uvs = Vectors(b, txuv, count, 2);
        }
        if (Piff.Child(chunks, -1, "COLR") is { } colr)
        {
            Le.Need(colr.Size == count * model.ColorBytes, "3DOB COLR does not match the vertex count.");
            model.Colors = new byte[count * 4];
            for (int v = 0; v < count; v++)
            {
                b.AsSpan(colr.Data + (v * model.ColorBytes), 3).CopyTo(model.Colors.AsSpan(v * 4));
                model.Colors[(v * 4) + 3] = model.ColorBytes == 4 ? b[colr.Data + (v * 4) + 3] : (byte)255;
            }
        }
        foreach (PiffChunk poly in Piff.Children(chunks, -1, "POLY"))
        {
            model.Polys.Add(Poly(b, poly, count));
        }
        if (Piff.Child(chunks, -1, "ID  ") is { } id)
        {
            model.Id = Encoding.Latin1.GetString(b, id.Data, id.Size).TrimEnd('\0', ' ');
        }
        int skin = Piff.IndexOf(chunks, -1, "FORM:SKIN");
        if (skin >= 0)
        {
            model.ReadSkin(b, chunks, skin, count);
        }
        int anim = Piff.IndexOf(chunks, -1, "FORM:ANIM");
        if (anim >= 0)
        {
            model.ReadAnimation(b, chunks, anim);
        }
        int lod = Piff.IndexOf(chunks, -1, "FORM:LOD ");
        model.LodLevels = lod < 0 ? 0 : Piff.Children(chunks, lod, "FORM:MESH").Count();
        return model;
    }
    private static float[] Vectors(byte[] b, PiffChunk chunk, int count, int components)
    {
        float[] values = new float[count * components];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Le.Fx(b, chunk.Data + (4 * i));
        }
        return values;
    }
    public static (int Mode, int Appearance, int[] Indices) Poly(byte[] b, PiffChunk chunk, int vertices)
    {
        Le.Need(chunk.Size >= 16, "POLY header is truncated.");
        int mode = (int)Le.U32(b, chunk.Data), count = (int)Le.U32(b, chunk.Data + 4), appearance = (int)Le.U32(b, chunk.Data + 8), type = (int)Le.U32(b, chunk.Data + 12);
        Le.Need(mode is 4 or 5 && type is 5121 or 5123, $"POLY mode {mode} / index type {type} is not supported.");
        int size = type == 5121 ? 1 : 2;
        Le.Need(chunk.Size == 16 + ((long)count * size), "POLY size does not match its index count.");
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = size == 1 ? b[chunk.Data + 16 + i] : Le.U16(b, chunk.Data + 16 + (2 * i));
            Le.Need(indices[i] < vertices, $"POLY index {indices[i]} is outside the {vertices} vertices.");
        }
        return (mode, appearance, indices);
    }
    public static IEnumerable<int> Triangles(int mode, int[] ix)
    {
        if (mode == 4)
        {
            for (int k = 0; k + 2 < ix.Length; k += 3)
            {
                yield return ix[k];
                yield return ix[k + 1];
                yield return ix[k + 2];
            }
            yield break;
        }
        for (int k = 0; k + 2 < ix.Length; k++)
        {
            (int a, int b, int c) = (k & 1) == 0 ? (ix[k], ix[k + 1], ix[k + 2]) : (ix[k + 1], ix[k], ix[k + 2]);
            if (a != b && b != c && a != c)
            {
                yield return a;
                yield return b;
                yield return c;
            }
        }
    }
    private void ReadTextures(byte[] b, List<PiffChunk> chunks)
    {
        if (Piff.Child(chunks, -1, "TEXL") is not { } texl)
        {
            return;
        }
        int p = texl.Data, end = texl.Data + texl.Size;
        while (p < end && b[p] != 0)
        {
            int nul = Array.IndexOf(b, (byte)0, p, end - p);
            Le.Need(nul >= 0, "TEXL name is not terminated.");
            Textures.Add(Encoding.Latin1.GetString(b, p, nul - p));
            p = nul + 1;
        }
    }
    private void ReadAppearances(byte[] b, List<PiffChunk> chunks)
    {
        if (Piff.Child(chunks, -1, "APPR") is { } appr)
        {
            int size = (int)Le.U32(b, appr.Data);
            Le.Need(size is 4 or 16 && (appr.Size - 4) % size == 0, "APPR record size is not 4 or 16.");
            for (int p = appr.Data + 4; p + size <= appr.Data + appr.Size; p += size)
            {
                Appearances.Add(Le.U16(b, p + 2) - 1);
            }
        }
        else if (Piff.Child(chunks, -1, "MATL") is { } matl)
        {
            for (int p = matl.Data; p + 24 <= matl.Data + matl.Size; p += 24)
            {
                Appearances.Add(Le.U16(b, p + 2) - 1);
            }
        }
        else if (Piff.Child(chunks, -1, "MAT2") is { } mat2)
        {
            for (int p = mat2.Data + 4; p + 28 <= mat2.Data + mat2.Size; p += 28)
            {
                Appearances.Add(Le.U16(b, p + 2) - 1);
            }
        }
    }
    public int TextureOf(int appearance)
    {
        int texture = appearance >= 1 && appearance <= Appearances.Count ? Appearances[appearance - 1] : -1;
        return texture >= 0 && texture < Textures.Count ? texture : Textures.Count == 1 && appearance >= 1 ? 0 : -1;
    }
    private void ReadSkin(byte[] b, List<PiffChunk> chunks, int skin, int vertices)
    {
        PiffChunk jnts = Piff.Child(chunks, skin, "JNTS") ?? throw new InvalidDataException("FORM:SKIN has no JNTS.");
        PiffChunk skel = Piff.Child(chunks, skin, "SKEL") ?? throw new InvalidDataException("FORM:SKIN has no SKEL.");
        int ni = (int)Le.U32(b, jnts.Data), nw = (int)Le.U32(b, jnts.Data + 4), nc = (int)Le.U32(b, jnts.Data + 8);
        int weightsAt = nw > 0 ? (12 + (2 * ni) + 3) & ~3 : 12 + (2 * ni);
        Le.Need(jnts.Size == weightsAt + (4L * nw) + (4L * nc), "JNTS size does not match its counts.");
        int bones = (int)Le.U32(b, skel.Data);
        Le.Need(skel.Size == 4 + (40L * bones), "SKEL size does not match its bone count.");
        Influences = new List<(int, float)>[vertices];
        for (int v = 0; v < vertices; v++)
        {
            Influences[v] = [];
        }
        int IndexAt(int k)
        {
            Le.Need(k >= 0 && k < ni, $"Skin entry {k} is outside the {ni} JNTS indices.");
            int vertex = Le.U16(b, jnts.Data + 12 + (2 * k));
            Le.Need(vertex < vertices, $"Skin vertex {vertex} is outside the {vertices} vertices.");
            return vertex;
        }
        for (int k = 0; k < bones; k++)
        {
            int at = skel.Data + 4 + (40 * k);
            PiffBone bone = new(Le.U32(b, at), Le.U32(b, at + 4), Le.I32(b, at + 8), Le.I32(b, at + 12), Le.I32(b, at + 16),
                Le.I32(b, at + 20), Le.I32(b, at + 24), [Le.Fx(b, at + 28), Le.Fx(b, at + 32), Le.Fx(b, at + 36)]);
            Bones.Add(bone);
            for (int j = 0; j < bone.Rigid; j++)
            {
                Influences[IndexAt(bone.RigidStart + j)].Add((k, 1f));
            }
            for (int j = 0; j < bone.Weighted; j++)
            {
                int w = bone.WeightStart + j;
                Le.Need(w >= 0 && w < nw, $"Skin weight {w} is outside the {nw} JNTS weights.");
                Influences[IndexAt(bone.WeightedStart + j)].Add((k, Le.Fx(b, jnts.Data + weightsAt + (4 * w))));
            }
        }
        for (int k = 0; k < nc; k++)
        {
            int at = jnts.Data + weightsAt + (4 * nw) + (4 * k);
            int source = Le.U16(b, at), destination = Le.U16(b, at + 2);
            Le.Need(source < vertices && destination < vertices, "JNTS copy points outside the mesh.");
            Influences[destination] = [.. Influences[source]];
        }
        foreach (PiffBone bone in Bones)
        {
            Le.Need(bone.Parent == uint.MaxValue || Bones.Exists(x => x.Id == bone.Parent), $"Bone {bone.Name} names a parent that is not in the skeleton.");
        }
    }
    private void ReadAnimation(byte[] b, List<PiffChunk> chunks, int anim)
    {
        if (Piff.Child(chunks, anim, "KEYF") is not { } keyf)
        {
            return;
        }
        int keys = (int)Le.U32(b, keyf.Data);
        Le.Need(keyf.Size == 12 + (4L * keys), "KEYF size does not match its key count.");
        KeyTimes = new int[keys];
        for (int k = 0; k < keys; k++)
        {
            KeyTimes[k] = Le.I32(b, keyf.Data + 12 + (4 * k));
        }
        if (Piff.Child(chunks, anim, "ARNG") is { } arng)
        {
            int count = (int)Le.U32(b, arng.Data);
            Le.Need(arng.Size == 4 + (12L * count), "ARNG size does not match its clip count.");
            for (int c = 0; c < count; c++)
            {
                int at = arng.Data + 4 + (12 * c);
                int first = (int)Le.U32(b, at + 4), last = (int)Le.U32(b, at + 8);
                Le.Need(first <= last && last < keys, $"ARNG clip {c} spans keys {first}-{last} of {keys}.");
                Clips.Add((Encoding.Latin1.GetString(b, at, 4).TrimEnd('\0', ' '), first, last));
            }
        }
        if (Piff.Child(chunks, anim, "BANI") is { } bani && IsSkinned)
        {
            int nt = (int)Le.U32(b, bani.Data), nb = Bones.Count;
            Le.Need(nt == 0 || nt == nb, "BANI translation count is neither zero nor the bone count.");
            Le.Need(bani.Size == 4 + (12L * nt * keys) + (16L * nb * keys), "BANI size does not match the keys and bones.");
            int p = bani.Data + 4;
            if (nt > 0)
            {
                Translations = new float[keys][];
                for (int k = 0; k < keys; k++)
                {
                    Translations[k] = new float[nt * 3];
                    for (int i = 0; i < nt * 3; i++, p += 4)
                    {
                        Translations[k][i] = Le.Fx(b, p);
                    }
                }
            }
            Rotations = new float[keys][];
            for (int k = 0; k < keys; k++)
            {
                Rotations[k] = new float[nb * 4];
                for (int i = 0; i < nb * 4; i++, p += 4)
                {
                    Rotations[k][i] = Le.Fx(b, p);
                }
            }
        }
        if (Piff.Child(chunks, anim, "XFRM") is { } xfrm)
        {
            Le.Need(Le.U32(b, xfrm.Data) == 2 && xfrm.Size == 4 + (12L * keys), "XFRM size does not match the key count.");
            Scales = new float[keys][];
            for (int k = 0; k < keys; k++)
            {
                Scales[k] = [Le.Fx(b, xfrm.Data + 4 + (12 * k)), Le.Fx(b, xfrm.Data + 8 + (12 * k)), Le.Fx(b, xfrm.Data + 12 + (12 * k))];
            }
        }
    }
    public SceneGeometry BuildScene(TextureSet textures, string name)
    {
        int count = VertexCount;
        float[] positions = new float[count * 3], normals = new float[Normals.Length], uvs = new float[Uvs.Length];
        for (int v = 0; v < count; v++)
        {
            (positions[v * 3], positions[(v * 3) + 1], positions[(v * 3) + 2]) = Axes.ToHost(Positions[v * 3], Positions[(v * 3) + 1], Positions[(v * 3) + 2]);
            if (normals.Length > 0)
            {
                (normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2]) = Axes.ToHost(Normals[v * 3], Normals[(v * 3) + 1], Normals[(v * 3) + 2]);
            }
            if (uvs.Length > 0)
            {
                uvs[v * 2] = Uvs[v * 2];
                uvs[(v * 2) + 1] = Axes.FlipV(Uvs[(v * 2) + 1]);
            }
        }
        List<int> indices = [];
        List<SceneSubmesh> submeshes = [];
        foreach ((int mode, int appearance, int[] ix) in Polys)
        {
            int first = indices.Count;
            indices.AddRange(Triangles(mode, ix));
            int texture = TextureOf(appearance);
            int slot = texture < 0 ? -1 : textures.Add(Textures[texture]);
            submeshes.Add(new SceneSubmesh(texture < 0 ? "untextured" : Path.GetFileNameWithoutExtension(Textures[texture]), first, indices.Count - first, slot));
        }
        (int[]? joints, float[]? weights) = IsSkinned ? SkinArrays() : (null, null);
        return new SceneGeometry
        {
            Meshes =
            [
                new SceneMesh
                {
                    Name = name,
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Colors = Colors,
                    Indices = [.. indices],
                    Joints = joints,
                    Weights = weights,
                    Submeshes = submeshes,
                },
            ],
            Textures = textures.Images,
            Joints = IsSkinned ? PiffAnimation.Joints(this) : [],
            Clips = IsSkinned && IsAnimated ? PiffAnimation.ClipsOf(this) : [],
        };
    }
    private (int[] Joints, float[] Weights) SkinArrays()
    {
        int count = VertexCount;
        int[] joints = new int[count * 4];
        float[] weights = new float[count * 4];
        for (int v = 0; v < count; v++)
        {
            (int Bone, float Weight)[] top = [.. Influences[v].OrderByDescending(x => x.Weight).Take(4)];
            float sum = top.Sum(x => x.Weight);
            for (int k = 0; k < top.Length; k++)
            {
                joints[(v * 4) + k] = top[k].Bone;
                weights[(v * 4) + k] = sum > 0 ? top[k].Weight / sum : 0;
            }
            if (top.Length == 0)
            {
                weights[v * 4] = 1;
            }
        }
        return (joints, weights);
    }
}
internal readonly record struct TrackChunk(int Material, int VertexBase, int TexBase, int ColourBase, int IndexSize, int Count, int IndexOffset);
internal sealed class TrackMesh
{
    public List<float> P { get; } = [];
    public List<float> T { get; } = [];
    public List<byte> C { get; } = [];
    public List<int> I { get; } = [];
    public Dictionary<(int, int, int), int> Seen { get; } = [];
}
internal sealed class PiffTrack
{
    private PiffTrack()
    {
    }
    public bool Cnk2Layout { get; private init; }
    public int Vertices { get; private init; }
    public int TexCoords { get; private init; }
    public int Colours { get; private init; }
    public List<string> Textures { get; } = [];
    public List<(int Texture, int Flags)> Materials { get; } = [];
    public List<TrackChunk> Chunks { get; } = [];
    public Dictionary<string, int> Counts { get; } = [];
    private float[] _positions = [];
    private byte[] _payload = [];
    private PiffChunk _tcor, _colr, _inds;
    public static PiffTrack Parse(byte[] b)
    {
        List<PiffChunk> chunks = Piff.Walk(b);
        Le.Need(Piff.FileType(b) == "SCEN", "PIFF file is not a SCEN track.");
        PiffChunk Need(string id)
        {
            return Piff.Child(chunks, -1, id) ?? throw new InvalidDataException($"SCEN has no {id} chunk.");
        }
        bool cnk2 = Piff.Child(chunks, -1, "VERS") is not null;
        PiffChunk vert = Need("VERT"), tcor = Need("TCOR"), colr = Need("COLR"), inds = Need("INDS");
        PiffTrack track = new()
        {
            Cnk2Layout = cnk2,
            Vertices = (int)Le.U32(b, vert.Data),
            TexCoords = (int)Le.U32(b, tcor.Data),
            Colours = (int)Le.U32(b, colr.Data + 4),
            _payload = b,
            _tcor = tcor,
            _colr = colr,
            _inds = inds,
        };
        Le.Need(vert.Size == 4 + (6L * track.Vertices) && tcor.Size == 4 + (4L * track.TexCoords) && colr.Size == 8 + (2L * track.Colours),
            "SCEN vertex, texcoord or colour chunk does not match its count.");
        PiffChunk txtr = Need("TXTR");
        int p = txtr.Data + 4;
        for (int i = 0; i < Le.U32(b, txtr.Data); i++)
        {
            p += cnk2 ? 1 : 0;
            int length = b[p];
            track.Textures.Add(Encoding.Latin1.GetString(b, p + 1, length));
            p += 1 + length;
        }
        Le.Need(p == txtr.Data + txtr.Size, "TXTR size does not match its names.");
        PiffChunk matl = Need("MATL");
        int materials = (int)Le.U32(b, matl.Data), recordSize = cnk2 ? 14 : 8;
        Le.Need(matl.Size == 4 + ((long)recordSize * materials), "SCEN MATL size does not match its count.");
        for (int i = 0; i < materials; i++)
        {
            int at = matl.Data + 4 + (recordSize * i);
            (int texture, int flags) = cnk2 ? (Le.U16(b, at + 6), Le.U16(b, at)) : (Le.U16(b, at), Le.U16(b, at + 2));
            Le.Need(texture < track.Textures.Count, $"SCEN material {i} names texture {texture} of {track.Textures.Count}.");
            track.Materials.Add((texture, flags));
        }
        PiffChunk chnk = Need("CHNK");
        int count = (int)Le.U32(b, chnk.Data), chunkSize = cnk2 ? 18 : 14;
        Le.Need(chnk.Size == 4 + ((long)chunkSize * count), "CHNK size does not match its count.");
        for (int i = 0; i < count; i++)
        {
            int at = chnk.Data + 4 + (chunkSize * i);
            int[] r = [.. Enumerable.Range(0, chunkSize / 2).Select(k => (int)Le.U16(b, at + (2 * k)))];
            TrackChunk record = cnk2 ? new(r[0], r[1], r[3], r[4], r[5], r[6], r[7]) : new(r[0], r[1], r[2], r[3], r[4], r[5], r[6]);
            Le.Need(record.Material < materials && record.IndexSize is 1 or 2 && 4 + (2L * record.IndexOffset) + ((long)record.Count * record.IndexSize) <= inds.Size,
                $"CHNK record {i} points outside the materials or the index data.");
            track.Chunks.Add(record);
        }
        float[] origins = new float[track.Vertices * 3];
        PiffChunk voff = Need("VOFF");
        int cells = (int)Le.U32(b, voff.Data);
        Le.Need(voff.Size == 4 + (20L * cells), "VOFF size does not match its count.");
        for (int c = 0; c < cells; c++)
        {
            int at = voff.Data + 4 + (20 * c);
            float x = Le.Fx(b, at), y = Le.Fx(b, at + 4), z = Le.Fx(b, at + 8);
            foreach ((int first, int n) in new[] { (Le.U16(b, at + 12), Le.U16(b, at + 14)), (Le.U16(b, at + 16), Le.U16(b, at + 18)) })
            {
                Le.Need(first + n <= track.Vertices, "VOFF run is outside the vertices.");
                for (int v = first; v < first + n; v++)
                {
                    (origins[v * 3], origins[(v * 3) + 1], origins[(v * 3) + 2]) = (x, y, z);
                }
            }
        }
        track._positions = new float[track.Vertices * 3];
        for (int i = 0; i < track._positions.Length; i++)
        {
            track._positions[i] = origins[i] + (Le.I16(b, vert.Data + 4 + (2 * i)) / 64f);
        }
        foreach (string id in new[] { "POSI", "CAMS", "PICK", "EMIT" })
        {
            if (Piff.Child(chunks, -1, id) is { } chunk)
            {
                track.Counts[id] = (int)Le.U32(b, chunk.Data);
            }
        }
        if (Piff.Child(chunks, -1, "LINE") is { } line)
        {
            track.Counts["LINE"] = Le.U16(b, line.Data);
        }
        track.Counts["QUAD"] = chunks.Count(c => c.Id == "FORM:QUAD");
        return track;
    }
    public SceneGeometry BuildScene(TextureSet textures)
    {
        byte[] b = _payload;
        Dictionary<int, TrackMesh> meshes = [];
        bool anyColour = false;
        foreach (TrackChunk chunk in Chunks)
        {
            (int texture, int flags) = Materials[chunk.Material];
            bool textured = (flags & 1) != 0, coloured = (flags & 0x10) != 0;
            anyColour |= coloured;
            int slot = textured ? textures.Add(Textures[texture]) : -1;
            if (!meshes.TryGetValue(slot, out TrackMesh? mesh))
            {
                meshes[slot] = mesh = new TrackMesh();
            }
            int at = _inds.Data + 4 + (2 * chunk.IndexOffset);
            int whole = chunk.Count - (chunk.Count % 3);
            for (int k = 0; k < whole; k++)
            {
                int i = chunk.IndexSize == 1 ? b[at + k] : Le.U16(b, at + (2 * k));
                int v = chunk.VertexBase + i, t = textured ? chunk.TexBase + i : -1, c = coloured ? chunk.ColourBase + i : -1;
                Le.Need(v < Vertices && t < TexCoords && c < Colours, "CHNK index points past the vertex, texcoord or colour data.");
                if (!mesh.Seen.TryGetValue((v, t, c), out int index))
                {
                    index = mesh.P.Count / 3;
                    mesh.Seen[(v, t, c)] = index;
                    (float x, float y, float z) = Axes.ToHost(_positions[v * 3], _positions[(v * 3) + 1], _positions[(v * 3) + 2]);
                    mesh.P.AddRange([x, y, z]);
                    mesh.T.AddRange(t < 0 ? [0f, 0f] : [Le.I16(b, _tcor.Data + 4 + (4 * t)) / 256f, Axes.FlipV(Le.I16(b, _tcor.Data + 6 + (4 * t)) / 256f)]);
                    ushort argb = c < 0 ? (ushort)0xFFFF : Le.U16(b, _colr.Data + 8 + (2 * c));
                    mesh.C.AddRange([(byte)(((argb >> 8) & 15) * 17), (byte)(((argb >> 4) & 15) * 17), (byte)((argb & 15) * 17), (byte)((argb >> 12) * 17)]);
                }
                mesh.I.Add(index);
            }
        }
        List<SceneMesh> result = [];
        foreach ((int slot, TrackMesh mesh) in meshes.OrderBy(x => x.Key))
        {
            string name = slot < 0 ? "untextured" : textures.Images[slot].Name;
            result.Add(new SceneMesh
            {
                Name = name,
                Positions = [.. mesh.P],
                Uvs = [.. mesh.T],
                Colors = anyColour ? [.. mesh.C] : [],
                Indices = [.. mesh.I],
                Submeshes = [new SceneSubmesh(name, 0, mesh.I.Count, slot)],
            });
        }
        return new SceneGeometry
        {
            Meshes = result,
            Textures = textures.Images,
            Lighting = anyColour ? SceneLighting.VertexBaked : SceneLighting.Shaded,
        };
    }
}
