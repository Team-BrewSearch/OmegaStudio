using System.Numerics;
using DWSSK.Formats.Core;
using DWSSK.Formats.Models;
using Omega.Plugins;

namespace DWSSK.Formats.Anim;

internal sealed record AnimKey(double Time, Vector3? T, Quaternion? R, Vector3? S);

internal sealed record AnimTrack(int Bone, List<AnimKey> Keys);

internal sealed record AnimClipData(string Name, int Frames, List<AnimTrack> Tracks);

internal sealed class AnimRig
{
    private readonly int[] _parents;
    private readonly Matrix4x4[] _inverseBind;
    private readonly (Vector3 T, Quaternion R, Vector3 S)[] _bind;
    private readonly double[] _k;

    public AnimRig(IReadOnlyList<GdBone> bones)
    {
        int n = bones.Count;
        _parents = [.. bones.Select(b => b.Parent ?? -1)];
        (Matrix4x4[] world, double[][] local) = GdScene.BoneWorlds(bones);
        _inverseBind = [.. world.Select(m => MatrixOps.InvertOrIdentity(m))];
        _bind = new (Vector3, Quaternion, Vector3)[n];
        for (int i = 0; i < n; i++)
        {
            double[] m = local[i];
            double[] cols = [.. Enumerable.Range(0, 3).Select(c => Math.Sqrt((m[c] * m[c]) + (m[4 + c] * m[4 + c]) + (m[8 + c] * m[8 + c])))];
            _bind[i] = (
                new Vector3((float)m[3], (float)m[7], (float)m[11]),
                QuatFromMatrix(m, cols),
                new Vector3((float)cols[0], (float)cols[1], (float)cols[2]));
        }
        double[] rig = new double[n];
        for (int i = 0; i < n; i++)
        {
            double[] inv = Mat.Inverse(bones[i].InverseBind);
            rig[i] = Math.Cbrt(Math.Abs(Mat.Det3(inv)));
        }
        _k = [.. Enumerable.Repeat(1.0 / 800, n)];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < n; c++)
            {
                if (_parents[c] == i)
                {
                    _k[c] = rig[i] / 800;
                }
            }
        }
        Names = [.. bones.Select(b => GdScene.BoneName(b.Hash))];
    }

    public IReadOnlyList<string> Names { get; }

    private static Quaternion QuatFromMatrix(double[] m, double[] cols)
    {
        double R(int r, int c)
        {
            return cols[c] == 0 ? 0 : m[(r * 4) + c] / cols[c];
        }
        double t = R(0, 0) + R(1, 1) + R(2, 2);
        double[] q = new double[4];
        if (t > 0)
        {
            double s = Math.Sqrt(t + 1) * 2;
            q = [(R(2, 1) - R(1, 2)) / s, (R(0, 2) - R(2, 0)) / s, (R(1, 0) - R(0, 1)) / s, s / 4];
        }
        else
        {
            int i = 0;
            for (int d = 1; d < 3; d++)
            {
                if (R(d, d) > R(i, i))
                {
                    i = d;
                }
            }
            int j = (i + 1) % 3;
            int k = (i + 2) % 3;
            double s = Math.Sqrt(1 + R(i, i) - R(j, j) - R(k, k)) * 2;
            q[i] = s / 4;
            q[j] = (R(j, i) + R(i, j)) / s;
            q[k] = (R(k, i) + R(i, k)) / s;
            q[3] = (R(k, j) - R(j, k)) / s;
        }
        return new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
    }

    public List<AnimClipData> Read(JObj doc)
    {
        List<AnimClipData> clips = [];
        foreach (object? ao in J.A(doc["animations"]))
        {
            JObj a = J.O(ao);
            List<AnimTrack> tracks = [];
            int maxFrame = 0;
            foreach (object? to in J.A(a["tracks"]))
            {
                JObj t = J.O(to);
                int bone = J.I(t["bone"]);
                List<object?> keys = J.A(t["keys"]);
                List<string> channels = [.. J.A(t["channels"]).Select(J.S)];
                if (keys.Count == 0 || bone >= _parents.Length || channels.Count == 0)
                {
                    continue;
                }
                List<AnimKey> list = [];
                Quaternion? prev = null;
                foreach (object? ko in keys)
                {
                    JObj k = J.O(ko);
                    int frame = J.I(k["frame"]);
                    maxFrame = Math.Max(maxFrame, frame);
                    Quaternion? r = null;
                    if (channels.Contains("rotation"))
                    {
                        List<object?> q = J.A(k["rotation"]);
                        double x = J.D(q[0]);
                        double y = J.D(q[1]);
                        double z = J.D(q[2]);
                        double w = J.D(q[3]);
                        double len = Math.Max(Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w)), 1e-12);
                        Quaternion qq = new((float)(x / len), (float)(y / len), (float)(z / len), (float)(w / len));
                        if (prev is { } p && Quaternion.Dot(qq, p) < 0)
                        {
                            qq = Quaternion.Negate(qq);
                        }
                        prev = qq;
                        r = qq;
                    }
                    Vector3? tr = null;
                    if (channels.Contains("translation"))
                    {
                        List<object?> v = J.A(k["translation"]);
                        double f = _k[bone];
                        tr = new Vector3((float)(J.D(v[0]) * f), (float)(J.D(v[1]) * f), (float)(J.D(v[2]) * f));
                    }
                    Vector3? sc = null;
                    if (channels.Contains("scale"))
                    {
                        List<object?> v = J.A(k["scale"]);
                        sc = new Vector3((float)J.D(v[0]), (float)J.D(v[1]), (float)J.D(v[2]));
                    }
                    list.Add(new AnimKey(frame / AnimPack.Fps, tr, r, sc));
                }
                tracks.Add(new AnimTrack(bone, list));
            }
            if (tracks.Count == 0)
            {
                continue;
            }
            string name = a.Get("full_name") as string ?? J.S(a["name"]);
            clips.Add(new AnimClipData(name, Math.Max(1, maxFrame), tracks));
        }
        return clips;
    }

    private Matrix4x4 Compose(int bone, AnimKey key)
    {
        (Vector3 bt, Quaternion br, Vector3 bs) = _bind[bone];
        return Matrix4x4.CreateScale(key.S ?? bs) * Matrix4x4.CreateFromQuaternion(key.R ?? br) * Matrix4x4.CreateTranslation(key.T ?? bt);
    }

    public AnimationClip ToClip(AnimClipData data)
    {
        List<AnimationTrack> tracks = [];
        foreach (AnimTrack t in data.Tracks)
        {
            float[][] matrices = new float[t.Keys.Count][];
            for (int i = 0; i < t.Keys.Count; i++)
            {
                matrices[i] = new float[16];
                MatrixOps.CopyTo(Compose(t.Bone, t.Keys[i]), matrices[i]);
            }
            tracks.Add(new AnimationTrack
            {
                Name = Names[t.Bone],
                Times = [.. t.Keys.Select(k => (float)(k.Time * AnimPack.Fps))],
                Matrices = matrices,
            });
        }
        return new AnimationClip { Name = data.Name, Frames = data.Frames, Fps = (float)AnimPack.Fps, Tracks = tracks };
    }

    private static AnimKey Sample(List<AnimKey> keys, double t)
    {
        if (t <= keys[0].Time || keys.Count == 1)
        {
            return keys[0];
        }
        if (t >= keys[^1].Time)
        {
            return keys[^1];
        }
        int b = 1;
        while (b < keys.Count - 1 && keys[b].Time < t)
        {
            b++;
        }
        AnimKey a = keys[b - 1];
        AnimKey c = keys[b];
        float u = c.Time > a.Time ? (float)((t - a.Time) / (c.Time - a.Time)) : 0f;
        return new AnimKey(
            t,
            a.T is { } at && c.T is { } ct ? Vector3.Lerp(at, ct, u) : a.T,
            a.R is { } ar && c.R is { } cr ? Quaternion.Slerp(ar, cr, u) : a.R,
            a.S is { } asv && c.S is { } cs ? Vector3.Lerp(asv, cs, u) : a.S);
    }

    public Matrix4x4[] Pose(AnimClipData clip, double seconds)
    {
        int n = _parents.Length;
        Matrix4x4[] local = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            (Vector3 bt, Quaternion br, Vector3 bs) = _bind[i];
            local[i] = Matrix4x4.CreateScale(bs) * Matrix4x4.CreateFromQuaternion(br) * Matrix4x4.CreateTranslation(bt);
        }
        foreach (AnimTrack t in clip.Tracks)
        {
            local[t.Bone] = Compose(t.Bone, Sample(t.Keys, seconds));
        }
        Matrix4x4[] world = new Matrix4x4[n];
        bool[] done = new bool[n];
        Matrix4x4 World(int i, int depth)
        {
            if (done[i])
            {
                return world[i];
            }
            int p = _parents[i];
            world[i] = p >= 0 && p < n && p != i && depth < n ? local[i] * World(p, depth + 1) : local[i];
            done[i] = true;
            return world[i];
        }
        Matrix4x4[] skin = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            skin[i] = _inverseBind[i] * World(i, 0);
        }
        return skin;
    }

    public static bool Skin(SceneGeometry scene, Matrix4x4[] skin, out float[] positions, out float[] normals)
    {
        int total = scene.VertexCount;
        positions = new float[total * 3];
        normals = new float[total * 3];
        int at = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            int count = mesh.VertexCount;
            for (int v = 0; v < count; v++)
            {
                Vector3 p = new(mesh.Positions[v * 3], mesh.Positions[(v * 3) + 1], mesh.Positions[(v * 3) + 2]);
                Vector3 nrm = mesh.Normals.Length == count * 3 ? new(mesh.Normals[v * 3], mesh.Normals[(v * 3) + 1], mesh.Normals[(v * 3) + 2]) : Vector3.UnitY;
                Vector3 sp = p;
                Vector3 sn = nrm;
                if (mesh.Joints is { } joints && mesh.Weights is { } weights)
                {
                    sp = Vector3.Zero;
                    sn = Vector3.Zero;
                    float wsum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = weights[(v * 4) + k];
                        int j = joints[(v * 4) + k];
                        if (w <= 0 || j < 0 || j >= skin.Length)
                        {
                            continue;
                        }
                        sp += Vector3.Transform(p, skin[j]) * w;
                        sn += Vector3.TransformNormal(nrm, skin[j]) * w;
                        wsum += w;
                    }
                    if (wsum <= 0)
                    {
                        sp = p;
                        sn = nrm;
                    }
                    else if (wsum < 0.999f)
                    {
                        sp /= wsum;
                    }
                }
                float len = sn.Length();
                if (len > 1e-8f)
                {
                    sn /= len;
                }
                positions[(at * 3) + 0] = sp.X;
                positions[(at * 3) + 1] = sp.Y;
                positions[(at * 3) + 2] = sp.Z;
                normals[(at * 3) + 0] = sn.X;
                normals[(at * 3) + 1] = sn.Y;
                normals[(at * 3) + 2] = sn.Z;
                at++;
            }
        }
        return true;
    }
}
