using System.Numerics;
using DWSSK.Formats.Core;
using Omega.Plugins;

namespace DWSSK.Formats.Models;

internal interface ITextureLookup
{
    ImageData? Find(uint wadHash);
}

internal sealed class GdPrimArrays
{
    public required float[] Positions { get; init; }
    public float[]? Normals { get; init; }
    public float[]? Uv0 { get; init; }
    public float[]? Uv1 { get; init; }
    public byte[]? Color0 { get; init; }
    public byte[]? Color1 { get; init; }
}

internal sealed class GdDrawArrays
{
    public required int[] Triangles { get; init; }
    public ushort[]? Joints { get; init; }
    public float[]? Weights { get; init; }
}

internal static class GdScene
{
    public static GdPrimArrays Arrays(GdModel gd, GdPrim p)
    {
        GdVertices vb = p.Vb;
        double[] pos = GdLayouts.Attr(vb, gd.Big, "POSITION")!;
        double[]? n = GdLayouts.Attr(vb, gd.Big, "NORMAL");
        float[]? normals = null;
        if (n is not null)
        {
            normals = new float[n.Length];
            for (int i = 0; i < vb.Count; i++)
            {
                float x = (float)n[i * 3];
                float y = (float)n[(i * 3) + 1];
                float z = (float)n[(i * 3) + 2];
                float len = MathF.Max(MathF.Sqrt((x * x) + (y * y) + (z * z)), 1e-8f);
                normals[i * 3] = x / len;
                normals[(i * 3) + 1] = y / len;
                normals[(i * 3) + 2] = z / len;
            }
        }
        return new GdPrimArrays
        {
            Positions = ToFloat(pos),
            Normals = normals,
            Uv0 = GdLayouts.Attr(vb, gd.Big, "TEXCOORD_0") is { } u0 ? ToFloat(u0) : null,
            Uv1 = GdLayouts.Attr(vb, gd.Big, "TEXCOORD_1") is { } u1 ? ToFloat(u1) : null,
            Color0 = GdLayouts.Attr(vb, gd.Big, "COLOR_0") is { } c0 ? ToBytes(c0) : null,
            Color1 = GdLayouts.Attr(vb, gd.Big, "COLOR_1") is { } c1 ? ToBytes(c1) : null,
        };
    }

    private static float[] ToFloat(double[] v)
    {
        float[] f = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            f[i] = (float)v[i];
        }
        return f;
    }

    private static byte[] ToBytes(double[] v)
    {
        byte[] f = new byte[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            f[i] = (byte)v[i];
        }
        return f;
    }

    public static GdDrawArrays? DrawArrays(GdModel gd, GdPrim p, GdDraw d, bool skinned)
    {
        int count = p.Vb.Count;
        int[] tri = d.Strip ? GdReader.StripToTris(d.Indices) : d.Indices;
        if (!d.Strip && tri.Length % 3 != 0)
        {
            throw new InvalidDataException($"a triangle list of {tri.Length} indices");
        }
        List<int> kept = new(tri.Length);
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            if (tri[t] < count && tri[t + 1] < count && tri[t + 2] < count)
            {
                kept.Add(tri[t]);
                kept.Add(tri[t + 1]);
                kept.Add(tri[t + 2]);
            }
        }
        if (kept.Count == 0)
        {
            return null;
        }
        if (!skinned)
        {
            return new GdDrawArrays { Triangles = [.. kept] };
        }
        ushort[] joints = new ushort[count * 4];
        float[] weights = new float[count * 4];
        double[]? j = GdLayouts.Attr(p.Vb, gd.Big, "JOINTS_0");
        if (j is null || d.Rigid)
        {
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    joints[(i * 4) + k] = (ushort)d.Bone;
                }
                weights[i * 4] = 1;
            }
        }
        else
        {
            double[] w = GdLayouts.Attr(p.Vb, gd.Big, "WEIGHTS_0")!;
            for (int i = 0; i < count; i++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    int local = (int)j[(i * 4) + k];
                    joints[(i * 4) + k] = d.Palette is { } pal ? (ushort)(local < pal.Count ? pal[local] : 0) : (ushort)local;
                    sum += (float)w[(i * 4) + k];
                }
                float div = MathF.Max(sum, 1);
                for (int k = 0; k < 4; k++)
                {
                    weights[(i * 4) + k] = (float)w[(i * 4) + k] / div;
                }
            }
        }
        return new GdDrawArrays { Triangles = [.. kept], Joints = joints, Weights = weights };
    }

    public static (Matrix4x4[] World, double[][] Local) BoneWorlds(IReadOnlyList<GdBone> bones)
    {
        double[][] world = new double[bones.Count][];
        for (int i = 0; i < bones.Count; i++)
        {
            double[] w = Mat.Inverse(Mat.Transpose(bones[i].InverseBind));
            double s = Math.Cbrt(Math.Abs(Mat.Det3(w)));
            double div = s > 1e-8 ? s : 1;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    w[(r * 4) + c] /= div;
                }
            }
            world[i] = w;
        }
        double[][] local = new double[bones.Count][];
        for (int i = 0; i < bones.Count; i++)
        {
            local[i] = bones[i].Parent is int parent && parent < bones.Count
                ? Mat.Mul(Mat.Inverse(world[parent]), world[i])
                : bones[i].Parent is null ? world[i] : throw new InvalidDataException($"bone {i} names parent {bones[i].Parent}, past the {bones.Count} bones");
        }
        return ([.. world.Select(Mat.ToRowVector)], local);
    }

    public static IReadOnlyList<SceneJoint> Joints(IReadOnlyList<GdBone> bones)
    {
        (Matrix4x4[] world, _) = BoneWorlds(bones);
        List<SceneJoint> joints = new(bones.Count);
        for (int i = 0; i < bones.Count; i++)
        {
            float[] bind = new float[16];
            MatrixOps.CopyTo(world[i], bind);
            joints.Add(new SceneJoint(BoneName(bones[i].Hash), bones[i].Parent ?? -1, bind));
        }
        return joints;
    }

    public static string BoneName(uint hash)
    {
        return $"bone_{Py.Hex8(hash)}";
    }

    public static string MeshName(string stem, GdModel gd, int zone)
    {
        return gd.Kind == "gdw" ? $"{stem}_zone{zone}" : stem;
    }

    public static SceneGeometry Build(GdModel gd, string stem, ITextureLookup? textures, int lod = 0)
    {
        if (lod >= gd.Lods.Count)
        {
            return new SceneGeometry();
        }
        bool skinned = gd.Bones.Count > 0;
        List<ImageData> images = [];
        Dictionary<int, (int Texture, bool Clip)> byMaterial = [];
        (int, bool) MaterialTexture(int material)
        {
            if (byMaterial.TryGetValue(material, out (int, bool) found))
            {
                return found;
            }
            (int Texture, bool Clip) result = (-1, false);
            if (textures is not null && material < gd.Materials.Count && gd.Materials[material].Passes.Count > 0)
            {
                foreach (GdSampler s in gd.Materials[material].Passes[0].Samplers)
                {
                    if (s.Usage != 0 || s.Texture >= gd.Textures.Count)
                    {
                        continue;
                    }
                    if (textures.Find(gd.Textures[s.Texture].Hash) is { } image)
                    {
                        images.Add(image);
                        result = (images.Count - 1, HasCutout(image));
                        break;
                    }
                }
            }
            byMaterial[material] = result;
            return result;
        }
        List<SceneMesh> meshes = [];
        foreach (GdPrim p in gd.Lods[lod])
        {
            GdPrimArrays a = Arrays(gd, p);
            List<(GdDraw Draw, GdDrawArrays Arrays)> draws = [];
            foreach (GdDraw d in p.Draws)
            {
                if (DrawArrays(gd, p, d, skinned) is { } da)
                {
                    draws.Add((d, da));
                }
            }
            if (draws.Count == 0)
            {
                continue;
            }
            if (!skinned)
            {
                foreach (IGrouping<int, (GdDraw Draw, GdDrawArrays Arrays)> zone in draws.GroupBy(x => x.Draw.Zone))
                {
                    List<int> indices = [];
                    List<SceneSubmesh> subs = [];
                    foreach ((GdDraw d, GdDrawArrays da) in zone)
                    {
                        (int tex, bool clip) = MaterialTexture(d.Material);
                        subs.Add(new SceneSubmesh(MaterialName(gd, d.Material), indices.Count, da.Triangles.Length, tex, clip));
                        indices.AddRange(da.Triangles);
                    }
                    meshes.Add(Mesh(MeshName(stem, gd, zone.Key), a, [.. indices], subs, null, null, null));
                }
                continue;
            }
            foreach ((GdDraw d, GdDrawArrays da) in draws)
            {
                Dictionary<int, int> remap = [];
                List<int> used = [];
                int[] tris = new int[da.Triangles.Length];
                for (int t = 0; t < tris.Length; t++)
                {
                    int v = da.Triangles[t];
                    if (!remap.TryGetValue(v, out int nv))
                    {
                        nv = used.Count;
                        remap[v] = nv;
                        used.Add(v);
                    }
                    tris[t] = nv;
                }
                (int tex, bool clip) = MaterialTexture(d.Material);
                SceneSubmesh sub = new(MaterialName(gd, d.Material), 0, tris.Length, tex, clip);
                meshes.Add(Mesh(stem, a, tris, [sub], used, da.Joints, da.Weights));
            }
        }
        return new SceneGeometry
        {
            Meshes = meshes,
            Textures = images,
            Joints = skinned ? Joints(gd.Bones) : [],
        };
    }

    public static string MaterialName(GdModel gd, int material)
    {
        return material < gd.Materials.Count ? gd.Materials[material].Name : $"material{material}";
    }

    private static bool HasCutout(ImageData image)
    {
        byte[] px = image.Rgba;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] < 128)
            {
                return true;
            }
        }
        return false;
    }

    private static SceneMesh Mesh(string name, GdPrimArrays a, int[] indices, List<SceneSubmesh> subs, List<int>? used, ushort[]? joints, float[]? weights)
    {
        if (used is null)
        {
            return new SceneMesh
            {
                Name = name,
                Positions = a.Positions,
                Normals = a.Normals ?? [],
                Uvs = a.Uv0 ?? [],
                Colors = a.Color0 ?? [],
                Indices = indices,
                Submeshes = subs,
            };
        }
        int n = used.Count;
        return new SceneMesh
        {
            Name = name,
            Positions = Gather(a.Positions, used, 3),
            Normals = a.Normals is { } nn ? Gather(nn, used, 3) : [],
            Uvs = a.Uv0 is { } uv ? Gather(uv, used, 2) : [],
            Colors = a.Color0 is { } c ? Gather(c, used, 4) : [],
            Indices = indices,
            Joints = joints is null ? null : [.. Gather(joints, used, 4).Select(x => (int)x)],
            Weights = weights is null ? null : Gather(weights, used, 4),
            Submeshes = subs,
        };
    }

    private static T[] Gather<T>(T[] src, List<int> used, int width)
    {
        T[] dst = new T[used.Count * width];
        for (int i = 0; i < used.Count; i++)
        {
            Array.Copy(src, used[i] * width, dst, i * width, width);
        }
        return dst;
    }
}

internal static class Mat
{
    public static double[] Identity()
    {
        return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    }

    public static double[] Transpose(double[] m)
    {
        double[] t = new double[16];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                t[(c * 4) + r] = m[(r * 4) + c];
            }
        }
        return t;
    }

    public static double[] Mul(double[] a, double[] b)
    {
        double[] m = new double[16];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++)
                {
                    s += a[(r * 4) + k] * b[(k * 4) + c];
                }
                m[(r * 4) + c] = s;
            }
        }
        return m;
    }

    public static double Det3(double[] m)
    {
        return (m[0] * ((m[5] * m[10]) - (m[6] * m[9])))
            - (m[1] * ((m[4] * m[10]) - (m[6] * m[8])))
            + (m[2] * ((m[4] * m[9]) - (m[5] * m[8])));
    }

    public static double[] Inverse(double[] m)
    {
        double[] a = (double[])m.Clone();
        double[] inv = Identity();
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[(col * 4) + col]);
            for (int r = col + 1; r < 4; r++)
            {
                double v = Math.Abs(a[(r * 4) + col]);
                if (v > best)
                {
                    best = v;
                    pivot = r;
                }
            }
            if (best == 0)
            {
                throw new InvalidDataException("singular matrix");
            }
            if (pivot != col)
            {
                for (int c = 0; c < 4; c++)
                {
                    (a[(col * 4) + c], a[(pivot * 4) + c]) = (a[(pivot * 4) + c], a[(col * 4) + c]);
                    (inv[(col * 4) + c], inv[(pivot * 4) + c]) = (inv[(pivot * 4) + c], inv[(col * 4) + c]);
                }
            }
            double d = a[(col * 4) + col];
            for (int c = 0; c < 4; c++)
            {
                a[(col * 4) + c] /= d;
                inv[(col * 4) + c] /= d;
            }
            for (int r = 0; r < 4; r++)
            {
                if (r == col)
                {
                    continue;
                }
                double f = a[(r * 4) + col];
                if (f == 0)
                {
                    continue;
                }
                for (int c = 0; c < 4; c++)
                {
                    a[(r * 4) + c] -= f * a[(col * 4) + c];
                    inv[(r * 4) + c] -= f * inv[(col * 4) + c];
                }
            }
        }
        return inv;
    }

    public static Matrix4x4 ToRowVector(double[] columnVector)
    {
        double[] t = Transpose(columnVector);
        return new Matrix4x4(
            (float)t[0], (float)t[1], (float)t[2], (float)t[3],
            (float)t[4], (float)t[5], (float)t[6], (float)t[7],
            (float)t[8], (float)t[9], (float)t[10], (float)t[11],
            (float)t[12], (float)t[13], (float)t[14], (float)t[15]);
    }
}
