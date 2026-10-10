using System.Numerics;
using Omega.Plugins;
namespace Polarbit.Formats;

/// <summary>
/// 3DOB skeletal animation. Each key holds, per bone, a rotation about the bone's model-space pivot and an optional
/// offset, both relative to the parent bone; the bind pose is the identity. Quaternions are stored w, x, y, z.
/// The parent-relative reading was chosen over an absolute one because it stretches skinned edges least in 27 of 49
/// models, and the stored sign over the conjugate because it puts the drivers' hands forward on the wheel.
/// </summary>
internal sealed class PiffAnimation
{
    public const float Fps = 30f;
    private readonly PiffModel _model;
    private readonly Dictionary<AnimationClip, (int First, int Last)> _ranges = [];
    public PiffAnimation(PiffModel model)
    {
        _model = model;
        Clips = model.IsAnimated ? Build(model, _ranges) : [];
    }
    public IReadOnlyList<AnimationClip> Clips { get; }

    public static IReadOnlyList<SceneJoint> Joints(PiffModel model)
    {
        List<SceneJoint> joints = [];
        foreach (PiffBone bone in model.Bones)
        {
            float[] bind = new float[16];
            MatrixOps.CopyTo(Matrix4x4.CreateTranslation(Host(bone.Pivot, 0)), bind);
            joints.Add(new SceneJoint(UniqueName(model, bone), model.Bones.FindIndex(x => x.Id == bone.Parent), bind));
        }
        return joints;
    }

    public static IReadOnlyList<AnimationClip> ClipsOf(PiffModel model)
    {
        return Build(model, []);
    }

    private static string UniqueName(PiffModel model, PiffBone bone)
    {
        return model.Bones.Count(x => x.Name == bone.Name) > 1 ? $"{bone.Name}_{model.Bones.IndexOf(bone)}" : bone.Name;
    }

    private static Vector3 Host(float[] v, int at)
    {
        (float x, float y, float z) = Axes.ToHost(v[at], v[at + 1], v[at + 2]);
        return new Vector3(x, y, z);
    }

    private static List<AnimationClip> Build(PiffModel model, Dictionary<AnimationClip, (int, int)> ranges)
    {
        List<AnimationClip> clips = [];
        foreach ((string name, int first, int last) in model.Clips)
        {
            float[] times = [.. Enumerable.Range(first, last - first + 1).Select(k => (model.KeyTimes[k] - model.KeyTimes[first]) * Fps / 1000f)];
            List<AnimationTrack> tracks = [];
            for (int b = 0; b < model.Bones.Count && model.Rotations is not null; b++)
            {
                PiffBone bone = model.Bones[b];
                PiffBone? parent = model.Bones.Find(x => x.Id == bone.Parent);
                float[][] matrices = new float[times.Length][];
                for (int k = first; k <= last; k++)
                {
                    float[] q = model.Rotations[k];
                    (float qx, float qy, float qz) = Axes.ToHost(q[(b * 4) + 1], q[(b * 4) + 2], q[(b * 4) + 3]);
                    Vector3 offset = Host(bone.Pivot, 0) - (parent is null ? Vector3.Zero : Host(parent.Pivot, 0));
                    if (model.Translations is { } t)
                    {
                        offset += Host(t[k], b * 3);
                    }
                    matrices[k - first] = new float[16];
                    MatrixOps.CopyTo(Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, q[b * 4])) * Matrix4x4.CreateTranslation(offset), matrices[k - first]);
                }
                tracks.Add(new AnimationTrack { Name = UniqueName(model, bone), Times = times, Matrices = matrices });
            }
            AnimationClip clip = new()
            {
                Name = name,
                Frames = System.Math.Max(1, (int)System.Math.Ceiling(times[^1])),
                Fps = Fps,
                Tracks = tracks,
            };
            ranges[clip] = (first, last);
            clips.Add(clip);
        }
        return clips;
    }

    /// <summary>Model-space bone matrices (row-vector convention) at a fractional key, parents composed first.</summary>
    private Matrix4x4[] Pose(int k0, int k1, float blend)
    {
        PiffModel m = _model;
        Matrix4x4[] world = new Matrix4x4[m.Bones.Count];
        bool[] done = new bool[world.Length];
        Matrix4x4 Of(int b)
        {
            if (done[b])
            {
                return world[b];
            }
            PiffBone bone = m.Bones[b];
            float[] a = m.Rotations![k0], c = m.Rotations[k1];
            Quaternion q = Quaternion.Slerp(
                new Quaternion(a[(b * 4) + 1], a[(b * 4) + 2], a[(b * 4) + 3], a[b * 4]),
                new Quaternion(c[(b * 4) + 1], c[(b * 4) + 2], c[(b * 4) + 3], c[b * 4]), blend);
            Vector3 pivot = new(bone.Pivot[0], bone.Pivot[1], bone.Pivot[2]);
            Vector3 offset = m.Translations is { } t
                ? Vector3.Lerp(new Vector3(t[k0][b * 3], t[k0][(b * 3) + 1], t[k0][(b * 3) + 2]), new Vector3(t[k1][b * 3], t[k1][(b * 3) + 1], t[k1][(b * 3) + 2]), blend)
                : Vector3.Zero;
            Matrix4x4 local = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(pivot + offset);
            int parent = m.Bones.FindIndex(x => x.Id == bone.Parent);
            done[b] = true;
            world[b] = parent < 0 || parent == b ? local : local * Of(parent);
            return world[b];
        }
        for (int b = 0; b < world.Length; b++)
        {
            _ = Of(b);
        }
        return world;
    }

    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        PiffModel m = _model;
        if (!_ranges.TryGetValue(clip, out (int First, int Last) range))
        {
            positions = [];
            normals = [];
            return false;
        }
        double ms = m.KeyTimes[range.First] + (seconds * 1000);
        int k0 = range.First;
        while (k0 < range.Last && m.KeyTimes[k0 + 1] <= ms)
        {
            k0++;
        }
        int k1 = System.Math.Min(k0 + 1, range.Last);
        float blend = k1 == k0 || m.KeyTimes[k1] == m.KeyTimes[k0] ? 0f
            : (float)System.Math.Clamp((ms - m.KeyTimes[k0]) / (m.KeyTimes[k1] - m.KeyTimes[k0]), 0, 1);
        Matrix4x4[]? bones = m.IsSkinned && m.Rotations is not null ? Pose(k0, k1, blend) : null;
        Vector3 scale = m.Scales is { } s
            ? Vector3.Lerp(new Vector3(s[k0][0], s[k0][1], s[k0][2]), new Vector3(s[k1][0], s[k1][1], s[k1][2]), blend)
            : Vector3.One;
        int count = m.VertexCount;
        positions = new float[count * 3];
        normals = new float[count * 3];
        for (int v = 0; v < count; v++)
        {
            Vector3 p = new(m.Positions[v * 3], m.Positions[(v * 3) + 1], m.Positions[(v * 3) + 2]);
            Vector3 n = m.Normals.Length > 0 ? new Vector3(m.Normals[v * 3], m.Normals[(v * 3) + 1], m.Normals[(v * 3) + 2]) : Vector3.Zero;
            if (bones is not null && m.Influences[v].Count > 0)
            {
                Vector3 sp = Vector3.Zero, sn = Vector3.Zero;
                foreach ((int bone, float weight) in m.Influences[v])
                {
                    sp += Vector3.Transform(p, bones[bone]) * weight;
                    sn += Vector3.TransformNormal(n, bones[bone]) * weight;
                }
                (p, n) = (sp, sn.LengthSquared() > 0 ? Vector3.Normalize(sn) : sn);
            }
            p *= scale;
            (positions[v * 3], positions[(v * 3) + 1], positions[(v * 3) + 2]) = Axes.ToHost(p.X, p.Y, p.Z);
            (normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2]) = Axes.ToHost(n.X, n.Y, n.Z);
        }
        return true;
    }
}
