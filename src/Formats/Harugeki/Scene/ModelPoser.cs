using System.Numerics;
using Harugeki.Formats.Math;
using Harugeki.Formats.Skinning;
using Omega.Plugins;
namespace Harugeki.Formats.Scene;
internal static class ModelPoser
{
    public static bool TryPose(
        RingModel model, Dictionary<RingMesh, SkinBinding?> bindings, AnimationClip clip,
        double seconds, out float[] positions, out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (model.Meshes.Count == 0)
        {
            positions = [];
            normals = [];
            return false;
        }
        float fps = clip.Fps > 0f ? clip.Fps : RingAnimation.Fps;
        Dictionary<string, Matrix4x4> world = SampleTracks(clip, (float)(seconds * fps));
        Matrix4x4[] palette = BuildPalette(model, world);
        int total = model.Meshes.Sum(m => m.VertexCount * 3);
        positions = new float[total];
        normals = new float[total];
        int at = 0;
        foreach (RingMesh mesh in model.Meshes)
        {
            if (!bindings.TryGetValue(mesh, out SkinBinding? binding))
            {
                bindings[mesh] = binding = SkinBinding.Build(model, mesh);
            }
            if (binding is null)
            {
                Matrix4x4 placement = world.TryGetValue(mesh.Name, out Matrix4x4 own)
                    ? own
                    : RigidFallback(model, mesh, palette);
                SkinRigid(mesh, placement, positions, normals, at);
            }
            else
            {
                Skin(mesh, binding, palette, positions, normals, at);
            }
            at += mesh.VertexCount * 3;
        }
        return true;
    }
    private static Dictionary<string, Matrix4x4> SampleTracks(AnimationClip clip, float frame)
    {
        Dictionary<string, Matrix4x4> world = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationTrack track in clip.Tracks)
        {
            if (track.Times.Length == 0)
            {
                continue;
            }
            int k0 = FindKeyframe(track.Times, frame);
            int k1 = System.Math.Min(k0 + 1, track.Times.Length - 1);
            float t0 = track.Times[k0];
            float t1 = track.Times[k1];
            float blend = System.Math.Clamp(t1 > t0 ? (frame - t0) / (t1 - t0) : 0f, 0f, 1f);
            world[track.Name] = MatrixOps.Blend(
                Transforms.Mirrored(track.Matrices[k0]),
                Transforms.Mirrored(track.Matrices[k1]),
                blend);
        }
        return world;
    }
    private static Matrix4x4[] BuildPalette(RingModel model, Dictionary<string, Matrix4x4> world)
    {
        Matrix4x4[] palette = new Matrix4x4[model.Bones.Count];
        for (int i = 0; i < palette.Length; i++)
        {
            RingBone bone = model.Bones[i];
            Matrix4x4 bind = MatrixOps.ToMatrix(bone.Bind);
            Matrix4x4 animated = world.TryGetValue(bone.Name, out Matrix4x4 track) ? track : bind;
            palette[i] = MatrixOps.InvertOrIdentity(bind) * animated;
        }
        return palette;
    }
    private static void Skin(
        RingMesh mesh, SkinBinding binding, Matrix4x4[] palette,
        float[] positions, float[] normals, int at)
    {
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int to = at + (i * 3);
            int skinVertex = mesh.VertexIds[i];
            if (!binding.TryGetInfluences(
                    skinVertex, out ReadOnlySpan<int> joints, out ReadOnlySpan<float> weights))
            {
                CopyRest(mesh, i, positions, normals, to);
                continue;
            }
            Vector3 restPosition = Read(mesh.SkinPositions, skinVertex);
            Vector3 restNormal = Read(mesh.SkinNormals, skinVertex);
            Vector3 position = Vector3.Zero;
            Vector3 normal = Vector3.Zero;
            for (int k = 0; k < SkinBinding.MaxInfluences; k++)
            {
                float weight = weights[k];
                int joint = joints[k];
                if (weight <= 0f || joint < 0 || joint >= palette.Length)
                {
                    continue;
                }
                position += Vector3.Transform(restPosition, palette[joint]) * weight;
                normal += Vector3.TransformNormal(restNormal, palette[joint]) * weight;
            }
            Write(positions, to, position);
            Write(normals, to, normal);
        }
    }
    private static void SkinRigid(
        RingMesh mesh, Matrix4x4 placement, float[] positions, float[] normals, int at)
    {
        int skinVertexCount = mesh.SkinPositions.Length / 3;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int to = at + (i * 3);
            int skinVertex = mesh.VertexIds[i];
            if (skinVertex < 0 || skinVertex >= skinVertexCount)
            {
                CopyRest(mesh, i, positions, normals, to);
                continue;
            }
            Write(positions, to, Vector3.Transform(Read(mesh.SkinPositions, skinVertex), placement));
            Write(normals, to, Vector3.TransformNormal(Read(mesh.SkinNormals, skinVertex), placement));
        }
    }
    private static Matrix4x4 RigidFallback(RingModel model, RingMesh mesh, Matrix4x4[] palette)
    {
        int count = mesh.SkinPositions.Length / 3;
        if (count == 0 || model.Bones.Count == 0)
        {
            return Matrix4x4.Identity;
        }
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = Read(mesh.SkinPositions, i);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            sum += p;
        }
        bool enclosesOrigin = min.X <= 0f && max.X >= 0f
            && min.Y <= 0f && max.Y >= 0f
            && min.Z <= 0f && max.Z >= 0f;
        if (enclosesOrigin)
        {
            return Matrix4x4.Identity;
        }
        Vector3 centre = sum / count;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int b = 0; b < model.Bones.Count; b++)
        {
            float[] bind = model.Bones[b].Bind;
            float d = Vector3.DistanceSquared(centre, new Vector3(bind[12], bind[13], bind[14]));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = b;
            }
        }
        return best >= 0 ? palette[best] : Matrix4x4.Identity;
    }
    private static int FindKeyframe(float[] times, float frame)
    {
        int low = 0;
        int high = times.Length - 1;
        int found = 0;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (times[mid] <= frame)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }
    private static void CopyRest(RingMesh mesh, int drawVertex, float[] positions, float[] normals, int to)
    {
        int from = drawVertex * 3;
        mesh.Positions.AsSpan(from, 3).CopyTo(positions.AsSpan(to));
        mesh.Normals.AsSpan(from, 3).CopyTo(normals.AsSpan(to));
    }
    private static Vector3 Read(float[] source, int vertex)
    {
        return new Vector3(source[vertex * 3], source[(vertex * 3) + 1], source[(vertex * 3) + 2]);
    }
    private static void Write(float[] destination, int at, in Vector3 value)
    {
        destination[at] = value.X;
        destination[at + 1] = value.Y;
        destination[at + 2] = value.Z;
    }
}
