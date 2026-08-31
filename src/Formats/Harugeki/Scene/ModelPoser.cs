using Harugeki.Formats.Math;
using Harugeki.Formats.Skinning;
using Omega.Plugins;
using System.Numerics;

namespace Harugeki.Formats.Scene;

/// <summary>
/// Evaluates a clip against a model and produces posed vertex arrays.
///
/// <para>
/// This is the plugin's half of the animation split. The host owns the clock, the
/// transport and the dope sheet, because those are the same for every game; how a
/// vertex follows a bone is this format's business and stays here.
/// </para>
///
/// <para>
/// The output layout is the one <see cref="IAnimatedAsset.TryPose"/> specifies:
/// three floats per draw vertex, meshes in order, vertices within each — which is
/// exactly the order <see cref="ModelScene"/> emits them in, so the host can pack a
/// posed frame through the same walk as the rest pose.
/// </para>
/// </summary>
internal static class ModelPoser
{
    /// <param name="model">The model being posed.</param>
    /// <param name="bindings">
    /// Resolved skin bindings, keyed by mesh, owned and kept by the caller.
    ///
    /// <para>
    /// Passed in rather than built here because building one inverts every bone's weight list
    /// and allocates two arrays the size of the mesh — work this used to redo for every mesh on
    /// every one of sixty frames a second, for a result that cannot change until the model is
    /// reparsed. The node that owns the model clears the cache when it does.
    /// </para>
    /// </param>
    /// <param name="clip">The clip to sample.</param>
    /// <param name="seconds">Where in the clip to sample it.</param>
    /// <param name="positions">Posed positions, three floats per draw vertex.</param>
    /// <param name="normals">Posed normals, in the same order.</param>
    public static bool TryPose(
        RingModel model, Dictionary<RingMesh, SkinBinding?> bindings, AnimationClip clip,
        double seconds, out float[] positions, out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        // A bone-less model is still animatable: the detachable accessories that ship
        // beside the alt costumes are a single rigid mesh driven by a track of their
        // own name, so only a model with nothing to draw is a no-op.
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
                // Rigid mesh. Its own track is an absolute world transform, so use
                // that whenever the clip carries one.
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

    /// <summary>Interpolates every track to the given frame time.</summary>
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

    /// <summary>
    /// The per-bone skinning matrices.
    ///
    /// <para>
    /// Keys are absolute world transforms, not parent-relative, so there is no chain
    /// accumulation: a bone's matrix is its own inverse bind followed by its own
    /// animated world transform. A bone with no track keeps its bind pose, which
    /// falls out of this as the identity.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Transforms every draw vertex by its weighted blend of bone matrices. A vertex
    /// nothing weights keeps the rest position the draw stream already holds.
    /// </summary>
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

    /// <summary>Places an unweighted mesh with a single transform.</summary>
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

    /// <summary>
    /// Transform for a rigid, unweighted mesh that the clip does not name in any
    /// track — an alt costume's hat or ear in one of the borrowed body clips.
    ///
    /// <para>
    /// Which answer is right depends on the space the mesh was authored in, and the
    /// rest positions say which. A mesh whose bounds enclose the origin (kk00/kk01,
    /// sister01's hat) is modelled in its own local space: only its own track can
    /// place it, and multiplying it by some bone's matrix throws it across the scene,
    /// so it stays where the static view draws it. A mesh that already sits out at
    /// the skeleton (hat_kyon01 up at head height) shares the bind-pose space and can
    /// ride the bone it is resting on.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The last key at or before <paramref name="frame"/>, or 0 when every key is later.
    ///
    /// <para>
    /// Binary search rather than a walk. Key times ascend, and this runs once per track per
    /// frame: a 49-bone clip with a few hundred keys a track made the scan real work on the UI
    /// thread, sixty times a second, for an answer three comparisons could give.
    /// </para>
    /// </summary>
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
