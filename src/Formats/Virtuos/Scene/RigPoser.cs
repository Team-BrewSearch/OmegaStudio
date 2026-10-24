using System.Numerics;
using Omega.Plugins;
namespace Virtuos.Formats.Scene;
internal static class RigPoser
{
    private static readonly float[] s_identity = Identity();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkeModel, float[][]> s_inverseRest = [];
    public static AnimationClip ToClip(string name, SkeModel skeleton, AniModel animation, float fps)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animation);
        List<AnimationTrack> tracks = new(animation.Tracks.Count);
        foreach (AniTrack track in animation.Tracks)
        {
            float[] times = new float[animation.FrameCount];
            float[][] matrices = new float[animation.FrameCount][];
            for (int frame = 0; frame < animation.FrameCount; frame++)
            {
                times[frame] = frame;
                float[] matrix = new float[16];
                for (int k = 0; k < 16; k++)
                {
                    matrix[k] = track.Frames[(frame * 16) + k] / 4096f;
                }
                matrices[frame] = Upright(skeleton, track.Bone, matrix);
            }
            tracks.Add(new AnimationTrack { Name = SkeModel.JointName(track.Bone), Times = times, Matrices = matrices });
        }
        return new AnimationClip
        {
            Name = name,
            Frames = animation.FrameCount,
            Fps = fps,
            Tracks = tracks,
        };
    }
    private static float[] Upright(SkeModel skeleton, int bone, float[] matrix)
    {
        if (bone < 0 || bone >= skeleton.Bones.Count || skeleton.Bones[bone].Parent >= 0)
        {
            return matrix;
        }
        Matrix4x4 turned = new Matrix4x4(
            matrix[0], matrix[1], matrix[2], matrix[3],
            matrix[4], matrix[5], matrix[6], matrix[7],
            matrix[8], matrix[9], matrix[10], matrix[11],
            matrix[12], matrix[13], matrix[14], matrix[15]) * SkeModel.Upright;
        return
        [
            turned.M11, turned.M12, turned.M13, turned.M14,
            turned.M21, turned.M22, turned.M23, turned.M24,
            turned.M31, turned.M32, turned.M33, turned.M34,
            turned.M41, turned.M42, turned.M43, turned.M44,
        ];
    }
    public static bool TryPose(
        SceneGeometry scene,
        SkeModel skeleton,
        AniModel animation,
        double frame,
        out float[] positions,
        out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animation);
        positions = [];
        normals = [];
        if (scene.Joints.Count != skeleton.Bones.Count || animation.FrameCount == 0)
        {
            return false;
        }
        float[][] delta = Delta(skeleton, animation, frame);
        int positionCount = 0;
        int normalCount = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            positionCount += mesh.VertexCount * 3;
            if (mesh.Normals.Length == mesh.Positions.Length)
            {
                normalCount += mesh.VertexCount * 3;
            }
        }
        float[] posed = new float[positionCount];
        float[] facing = new float[normalCount];
        int p = 0;
        int n = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            bool hasNormals = mesh.Normals.Length == mesh.Positions.Length;
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                int joint = mesh.Joints is { } bound && (vertex * 4) < bound.Length ? bound[vertex * 4] : -1;
                float[] matrix = joint >= 0 && joint < delta.Length ? delta[joint] : s_identity;
                (posed[p], posed[p + 1], posed[p + 2]) = Apply(
                    matrix,
                    mesh.Positions[vertex * 3],
                    mesh.Positions[(vertex * 3) + 1],
                    mesh.Positions[(vertex * 3) + 2],
                    true);
                p += 3;
                if (hasNormals)
                {
                    (facing[n], facing[n + 1], facing[n + 2]) = Apply(
                        matrix,
                        mesh.Normals[vertex * 3],
                        mesh.Normals[(vertex * 3) + 1],
                        mesh.Normals[(vertex * 3) + 2],
                        false);
                    n += 3;
                }
            }
        }
        positions = posed;
        normals = facing;
        return true;
    }
    private static float[][] Delta(SkeModel skeleton, AniModel animation, double frame)
    {
        int bones = skeleton.Bones.Count;
        float[][] local = new float[bones][];
        for (int bone = 0; bone < bones; bone++)
        {
            local[bone] = new float[16];
            for (int k = 0; k < 16; k++)
            {
                local[bone][k] = skeleton.Bones[bone].Matrix[k] / 4096f;
            }
        }
        int last = animation.FrameCount - 1;
        double held = Math.Clamp(frame, 0, animation.FrameCount);
        int before = Math.Clamp((int)Math.Floor(held), 0, last);
        int after = before < last ? before + 1 : 0;
        float mix = (float)(held - before);
        foreach (AniTrack track in animation.Tracks)
        {
            if (track.Bone < 0 || track.Bone >= bones)
            {
                continue;
            }
            local[track.Bone] = Blend(track, before, after, mix);
        }
        float[][] world = new float[bones][];
        for (int bone = 0; bone < bones; bone++)
        {
            world[bone] = local[bone];
            for (int parent = skeleton.Bones[bone].Parent; parent >= 0; parent = skeleton.Bones[parent].Parent)
            {
                world[bone] = Multiply(world[bone], local[parent]);
            }
        }
        float[][] inverse = s_inverseRest.GetValue(skeleton, InverseRest);
        float[][] delta = new float[bones][];
        double[] posed = new double[16];
        for (int bone = 0; bone < bones; bone++)
        {
            for (int k = 0; k < 16; k++)
            {
                posed[k] = world[bone][k];
            }
            delta[bone] = Multiply(inverse[bone], SkeModel.Pose(posed));
        }
        return delta;
    }
    private static float[][] InverseRest(SkeModel skeleton)
    {
        float[][] inverse = new float[skeleton.Bones.Count][];
        for (int bone = 0; bone < inverse.Length; bone++)
        {
            inverse[bone] = Invert(skeleton.RestMatrix(bone));
        }
        return inverse;
    }
    private static float[] Blend(AniTrack track, int before, int after, float mix)
    {
        Matrix4x4 first = Read(track, before);
        if (mix <= 0f || after == before)
        {
            return Flatten(first);
        }
        Matrix4x4 second = Read(track, after);
        Vector3 move = Vector3.Lerp(first.Translation, second.Translation, mix);
        Quaternion turn = Quaternion.Slerp(
            Quaternion.CreateFromRotationMatrix(first), Quaternion.CreateFromRotationMatrix(second), mix);
        Matrix4x4 mixed = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(turn));
        mixed.Translation = move;
        return Flatten(mixed);
    }
    private static Matrix4x4 Read(AniTrack track, int frame)
    {
        int at = frame * 16;
        return new Matrix4x4(
            track.Frames[at] / 4096f, track.Frames[at + 1] / 4096f,
            track.Frames[at + 2] / 4096f, track.Frames[at + 3] / 4096f,
            track.Frames[at + 4] / 4096f, track.Frames[at + 5] / 4096f,
            track.Frames[at + 6] / 4096f, track.Frames[at + 7] / 4096f,
            track.Frames[at + 8] / 4096f, track.Frames[at + 9] / 4096f,
            track.Frames[at + 10] / 4096f, track.Frames[at + 11] / 4096f,
            track.Frames[at + 12] / 4096f, track.Frames[at + 13] / 4096f,
            track.Frames[at + 14] / 4096f, track.Frames[at + 15] / 4096f);
    }
    private static float[] Flatten(in Matrix4x4 matrix)
    {
        return
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
    }
    private static float[] Identity()
    {
        return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    }
    private static (float X, float Y, float Z) Apply(
        float[] matrix, float x, float y, float z, bool translate)
    {
        float px = (x * matrix[0]) + (y * matrix[4]) + (z * matrix[8]);
        float py = (x * matrix[1]) + (y * matrix[5]) + (z * matrix[9]);
        float pz = (x * matrix[2]) + (y * matrix[6]) + (z * matrix[10]);
        return translate ? (px + matrix[12], py + matrix[13], pz + matrix[14]) : (px, py, pz);
    }
    private static float[] Multiply(float[] child, float[] parent)
    {
        float[] product = new float[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += child[(row * 4) + k] * parent[(k * 4) + column];
                }
                product[(row * 4) + column] = sum;
            }
        }
        return product;
    }
    private static float[] Invert(float[] matrix)
    {
        float[] inverse = Identity();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                inverse[(row * 4) + column] = matrix[(column * 4) + row];
            }
        }
        for (int axis = 0; axis < 3; axis++)
        {
            inverse[12 + axis] = -((matrix[12] * inverse[axis])
                + (matrix[13] * inverse[4 + axis])
                + (matrix[14] * inverse[8 + axis]));
        }
        return inverse;
    }
}
