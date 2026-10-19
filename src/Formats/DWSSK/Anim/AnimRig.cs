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
    public const float TranslationScale = 0.00125f;
    private readonly int[] _parents;
    private readonly Matrix4x4[] _inverseBind;
    private readonly (Vector3 T, Quaternion R, Vector3 S)[] _bind;
    public AnimRig(IReadOnlyList<GdBone> bones)
    {
        _parents = [.. bones.Select(b => b.Parent ?? -1)];
        _inverseBind = [.. bones.Select(b => Row(b.InverseBind))];
        _bind = [.. bones.Select(Rest)];
        Names = [.. bones.Select(b => GdScene.BoneName(b.Hash))];
    }
    public IReadOnlyList<string> Names { get; }
    public static (Vector3 T, Quaternion R, Vector3 S) Rest(GdBone b)
    {
        return (
            new Vector3((float)b.Translation[0], (float)b.Translation[1], (float)b.Translation[2]) * TranslationScale,
            new Quaternion((float)b.Rotation[0], (float)b.Rotation[1], (float)b.Rotation[2], (float)b.Rotation[3]),
            new Vector3((float)b.Scale[0], (float)b.Scale[1], (float)b.Scale[2]));
    }
    private static float Fma(float a, float b, float c)
    {
        return (float)Math.FusedMultiplyAdd(a, b, (double)c);
    }
    private static float Add(float a, float b)
    {
        return (float)((double)a + b);
    }
    public static Matrix4x4 Local((Vector3 T, Quaternion R, Vector3 S) p)
    {
        Quaternion q = p.R;
        float xx = q.X * q.X;
        float yy = q.Y * q.Y;
        float zz = q.Z * q.Z;
        float xy = q.X * q.Y;
        float zw = q.Z * q.W;
        float xz = q.X * q.Z;
        float yw = q.Y * q.W;
        float xw = q.X * q.W;
        float yz = q.Y * q.Z;
        return new Matrix4x4(
            Fma(-2, Add(yy, zz), 1) * p.S.X, 2 * Add(xy, zw) * p.S.X, 2 * Add(xz, -yw) * p.S.X, 0,
            2 * Add(xy, -zw) * p.S.Y, Fma(-2, Add(xx, zz), 1) * p.S.Y, 2 * Add(yz, xw) * p.S.Y, 0,
            2 * Add(xz, yw) * p.S.Z, 2 * Add(yz, -xw) * p.S.Z, Fma(-2, Add(xx, yy), 1) * p.S.Z, 0,
            p.T.X, p.T.Y, p.T.Z, 1);
    }
    public static Matrix4x4 Mul(Matrix4x4 a, Matrix4x4 b)
    {
        static float E(float a0, float a1, float a2, float b0, float b1, float b2)
        {
            return Fma(a2, b2, Fma(a0, b0, a1 * b1));
        }
        return new Matrix4x4(
            E(a.M11, a.M12, a.M13, b.M11, b.M21, b.M31), E(a.M11, a.M12, a.M13, b.M12, b.M22, b.M32), E(a.M11, a.M12, a.M13, b.M13, b.M23, b.M33), 0,
            E(a.M21, a.M22, a.M23, b.M11, b.M21, b.M31), E(a.M21, a.M22, a.M23, b.M12, b.M22, b.M32), E(a.M21, a.M22, a.M23, b.M13, b.M23, b.M33), 0,
            E(a.M31, a.M32, a.M33, b.M11, b.M21, b.M31), E(a.M31, a.M32, a.M33, b.M12, b.M22, b.M32), E(a.M31, a.M32, a.M33, b.M13, b.M23, b.M33), 0,
            Add(b.M41, E(a.M41, a.M42, a.M43, b.M11, b.M21, b.M31)), Add(b.M42, E(a.M41, a.M42, a.M43, b.M12, b.M22, b.M32)), Add(b.M43, E(a.M41, a.M42, a.M43, b.M13, b.M23, b.M33)), 1);
    }
    private static Matrix4x4 Row(double[] m)
    {
        return new Matrix4x4(
            (float)m[0], (float)m[1], (float)m[2], (float)m[3],
            (float)m[4], (float)m[5], (float)m[6], (float)m[7],
            (float)m[8], (float)m[9], (float)m[10], (float)m[11],
            (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
    }
    private static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float dot = Fma(a.W, b.W, Fma(a.Z, b.Z, Fma(a.X, b.X, a.Y * b.Y)));
        if (dot < 0)
        {
            a = Quaternion.Negate(a);
            dot = -dot;
        }
        float wa = Add(1, -t);
        float wb = t;
        if (Add(1, -dot) > 0.0001f)
        {
            float th = (float)Math.Acos(dot);
            float sn = (float)Math.Sin(th);
            wa = (float)((double)(float)Math.Sin(wa * th) / sn);
            wb = (float)((double)(float)Math.Sin(t * th) / sn);
        }
        return new Quaternion(Fma(wa, a.X, wb * b.X), Fma(wa, a.Y, wb * b.Y), Fma(wa, a.Z, wb * b.Z), Fma(wa, a.W, wb * b.W));
    }
    private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        return new Vector3(Fma(t, Add(b.X, -a.X), a.X), Fma(t, Add(b.Y, -a.Y), a.Y), Fma(t, Add(b.Z, -a.Z), a.Z));
    }
    public List<AnimClipData> Read(JObj doc)
    {
        List<AnimClipData> clips = [];
        foreach (object? ao in J.A(doc["animations"]))
        {
            JObj a = J.O(ao);
            List<AnimTrack> tracks = [];
            int maxFrame = 0;
            float range = (float)J.D(a["unknown_48"]);
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
                foreach (object? ko in keys)
                {
                    JObj k = J.O(ko);
                    int frame = J.I(k["frame"]);
                    maxFrame = Math.Max(maxFrame, frame);
                    Quaternion? r = null;
                    if (channels.Contains("rotation"))
                    {
                        List<object?> q = J.A(k["rotation"]);
                        r = new((float)J.D(q[0]), (float)J.D(q[1]), (float)J.D(q[2]), (float)J.D(q[3]));
                    }
                    Vector3? tr = null;
                    if (channels.Contains("translation"))
                    {
                        List<object?> v = J.A(k["translation"]);
                        tr = new Vector3((float)J.D(v[0]), (float)J.D(v[1]), (float)J.D(v[2])) * TranslationScale;
                    }
                    Vector3? sc = null;
                    if (channels.Contains("scale"))
                    {
                        List<object?> v = J.A(k["scale"]);
                        sc = new Vector3((float)J.D(v[0]), (float)J.D(v[1]), (float)J.D(v[2]));
                    }
                    list.Add(new AnimKey(Math.Min(range, range * (float)((double)frame / 255)), tr, r, sc));
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
        return Local((key.T ?? bt, key.R ?? br, key.S ?? bs));
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
        float u = c.Time > a.Time ? (float)((double)Add((float)t, -(float)a.Time) / Add((float)c.Time, -(float)a.Time)) : 0f;
        return new AnimKey(
            t,
            a.T is { } at && c.T is { } ct ? Lerp(at, ct, u) : a.T,
            a.R is { } ar && c.R is { } cr ? Slerp(ar, cr, u) : a.R,
            a.S is { } asv && c.S is { } cs ? Lerp(asv, cs, u) : a.S);
    }
    public Matrix4x4[] Pose(AnimClipData clip, double seconds)
    {
        int n = _parents.Length;
        Matrix4x4[] local = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            local[i] = Local(_bind[i]);
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
            world[i] = p >= 0 && p < n && p != i && depth < n ? Mul(local[i], World(p, depth + 1)) : local[i];
            done[i] = true;
            return world[i];
        }
        Matrix4x4[] skin = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            skin[i] = Mul(_inverseBind[i], World(i, 0));
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
