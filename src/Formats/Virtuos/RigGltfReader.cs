using Omega.Plugins.Gltf;
using System.Numerics;
using System.Text.Json;
namespace Virtuos.Formats;

public static class RigGltfReader
{
    private const float Fps = AniModel.Fps;
    public static byte[] ReadSkeleton(IGltfSource source, ReadOnlySpan<byte> template, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(warn);
        SkeModel current = SkeModel.Parse(template);
        int[] joints = Joints(source);
        if (joints.Length != current.Bones.Count)
        {
            throw new InvalidDataException(
                $"the model has {joints.Length} joints where the skeleton has {current.Bones.Count}; "
                + "a skeleton keeps its bone count, so add or remove nothing and only move the joints");
        }
        JsonElement[] nodes = GltfJson.Array(source.Root, "nodes");
        Dictionary<int, int> boneOf = [];
        for (int bone = 0; bone < joints.Length; bone++)
        {
            boneOf[joints[bone]] = bone;
        }
        int[] parents = Parents(nodes, joints, boneOf);
        Matrix4x4[] placed = new Matrix4x4[joints.Length];
        for (int bone = 0; bone < joints.Length; bone++)
        {
            Matrix4x4 world = Local(nodes[joints[bone]]);
            for (int parent = parents[bone]; parent >= 0; parent = parents[parent])
            {
                world *= Local(nodes[joints[parent]]);
            }
            placed[bone] = world * SkeModel.Downright;
        }
        List<SkeBone> bones = new(joints.Length);
        for (int bone = 0; bone < joints.Length; bone++)
        {
            int parent = parents[bone];
            Matrix4x4 stored = current.BonesArePlaced || parent < 0
                ? placed[bone]
                : placed[bone] * Invert(placed[parent]);
            bones.Add(new SkeBone
            {
                Parent = parent,
                Matrix = Fixed(stored),
                Upward = current.BonesArePlaced
                    ? Upward(current, bone, parent, stored, placed)
                    : [],
            });
        }
        for (int bone = 0; bone < bones.Count; bone++)
        {
            if (bones[bone].Parent >= bone)
            {
                throw new InvalidDataException(
                    $"joint {bone} comes before its parent {bones[bone].Parent}; a skeleton lists parents first");
            }
        }
        warn($"{bones.Count} joints read from the model.");
        byte[] rebuilt = WriteSkeleton(bones, current.RecordSize);
        return Same(template, rebuilt) ? template.ToArray() : rebuilt;
    }
    public static byte[] ReadAnimation(
        IGltfSource source, ReadOnlySpan<byte> template, string wanted, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(warn);
        AniModel current = AniModel.Parse(template);
        JsonElement[] animations = GltfJson.Array(source.Root, "animations");
        if (animations.Length == 0)
        {
            throw new InvalidDataException("the model holds no animation to read");
        }
        JsonElement clip = animations[0];
        foreach (JsonElement candidate in animations)
        {
            if (candidate.TryGetProperty("name", out JsonElement name)
                && string.Equals(name.GetString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                clip = candidate;
            }
        }
        int[] joints = Joints(source);
        Dictionary<int, int> boneOf = [];
        for (int bone = 0; bone < joints.Length; bone++)
        {
            boneOf[joints[bone]] = bone;
        }
        int[] parents = Parents(GltfJson.Array(source.Root, "nodes"), joints, boneOf);
        JsonElement[] samplers = GltfJson.Array(clip, "samplers");
        Dictionary<int, Channel> channels = [];
        float duration = 0;
        float spacing = float.MaxValue;
        foreach (JsonElement channel in GltfJson.Array(clip, "channels"))
        {
            if (!channel.TryGetProperty("target", out JsonElement target)
                || !target.TryGetProperty("node", out JsonElement node)
                || !boneOf.TryGetValue(node.GetInt32(), out int bone)
                || !channel.TryGetProperty("sampler", out JsonElement index)
                || index.GetInt32() >= samplers.Length)
            {
                continue;
            }
            JsonElement sampler = samplers[index.GetInt32()];
            float[] times = source.ReadAccessor(sampler.GetProperty("input").GetInt32(), 1);
            string path = target.GetProperty("path").GetString() ?? string.Empty;
            int width = path == "rotation" ? 4 : 3;
            float[] values = source.ReadAccessor(sampler.GetProperty("output").GetInt32(), width);
            if (!channels.TryGetValue(bone, out Channel? found))
            {
                channels[bone] = found = new Channel();
            }
            found.Set(path, times, values);
            duration = Math.Max(duration, times.Length > 0 ? times[^1] : 0);
            spacing = times.Length > 1 ? Math.Min(spacing, times[^1] / (times.Length - 1)) : spacing;
        }
        if (channels.Count == 0)
        {
            throw new InvalidDataException(
                "no animation channel drives a joint of this skeleton, so there is nothing to write");
        }
        float rate = spacing is > 0f and < float.MaxValue ? 1f / spacing : Fps;
        int frames = Math.Max(1, (int)MathF.Round(duration * rate) + 1);
        List<AniTrack> tracks = [];
        foreach ((int bone, Channel channel) in channels.OrderBy(pair => pair.Key))
        {
            int[] values = new int[frames * 16];
            for (int frame = 0; frame < frames; frame++)
            {
                Matrix4x4 pose = channel.Sample(frame / rate);
                int[] matrix = Fixed(Stored(pose, parents[bone]));
                matrix.CopyTo(values, frame * 16);
            }
            tracks.Add(new AniTrack { Bone = bone, Offset = 0, Frames = values });
        }
        warn($"{tracks.Count} track(s) over {frames} frames read from '{clip.GetProperty("name").GetString()}'.");
        byte[] rebuilt = Rebuild(current, frames, tracks);
        return Same(template, rebuilt) ? template.ToArray() : rebuilt;
    }
    private static int[] Parents(JsonElement[] nodes, int[] joints, Dictionary<int, int> boneOf)
    {
        int[] parents = new int[joints.Length];
        Array.Fill(parents, -1);
        for (int candidate = 0; candidate < nodes.Length; candidate++)
        {
            if (!boneOf.TryGetValue(candidate, out int parent))
            {
                continue;
            }
            foreach (JsonElement child in GltfJson.Array(nodes[candidate], "children"))
            {
                if (boneOf.TryGetValue(child.GetInt32(), out int bone))
                {
                    parents[bone] = parent;
                }
            }
        }
        return parents;
    }
    private static Matrix4x4 Stored(Matrix4x4 local, int parent)
    {
        return parent < 0 ? local * SkeModel.Downright : local;
    }
    private static int[] Upward(
        SkeModel current, int bone, int parent, Matrix4x4 stored, Matrix4x4[] placed)
    {
        SkeBone was = current.Bones[bone];
        Matrix4x4 before = ToMatrix(was.Matrix);
        if (Near(before, stored) && was.Upward.Length == 16)
        {
            return was.Upward;
        }
        Matrix4x4 target = parent >= 0
            ? placed[parent]
            : before * ToMatrix(was.Upward.Length == 16 ? was.Upward : Identity());
        return Fixed(Invert(stored) * target);
    }
    private static int[] Identity()
    {
        return [4096, 0, 0, 0, 0, 4096, 0, 0, 0, 0, 4096, 0, 0, 0, 0, 4096];
    }
    private static Matrix4x4 ToMatrix(int[] fixedPoint)
    {
        return new Matrix4x4(
            fixedPoint[0] / 4096f, fixedPoint[1] / 4096f, fixedPoint[2] / 4096f, fixedPoint[3] / 4096f,
            fixedPoint[4] / 4096f, fixedPoint[5] / 4096f, fixedPoint[6] / 4096f, fixedPoint[7] / 4096f,
            fixedPoint[8] / 4096f, fixedPoint[9] / 4096f, fixedPoint[10] / 4096f, fixedPoint[11] / 4096f,
            fixedPoint[12] / 4096f, fixedPoint[13] / 4096f, fixedPoint[14] / 4096f, fixedPoint[15] / 4096f);
    }
    private static bool Near(in Matrix4x4 left, in Matrix4x4 right)
    {
        int[] a = Fixed(left);
        int[] b = Fixed(right);
        for (int k = 0; k < 16; k++)
        {
            if (Math.Abs(a[k] - b[k]) > 2)
            {
                return false;
            }
        }
        return true;
    }
    private static Matrix4x4 Invert(in Matrix4x4 matrix)
    {
        return Matrix4x4.Invert(matrix, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
    }
    private static byte[] WriteSkeleton(List<SkeBone> bones, int record)
    {
        byte[] output = new byte[SkeModel.HeaderSize + (bones.Count * record)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)bones.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(4), SkeModel.HeaderSize);
        for (int i = 0; i < bones.Count; i++)
        {
            int at = SkeModel.HeaderSize + (i * record);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                output.AsSpan(at), bones[i].Parent);
            for (int k = 0; k < 16; k++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(at + 4 + (k * 4)), bones[i].Matrix[k]);
            }
            for (int k = 0; k < bones[i].Upward.Length && record == SkeBone.WideSize; k++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(at + 68 + (k * 4)), bones[i].Upward[k]);
            }
        }
        return output;
    }
    private static byte[] Rebuild(AniModel current, int frames, List<AniTrack> tracks)
    {
        return AniModel.Write(current.Header, current.BoneCount, frames, tracks);
    }
    private static bool Same(ReadOnlySpan<byte> template, ReadOnlySpan<byte> rebuilt)
    {
        if (template.Length != rebuilt.Length)
        {
            return false;
        }
        for (int at = 0; at < template.Length; at += 4)
        {
            int left = (int)Binary.BinaryRead.U32(template, at);
            int right = (int)Binary.BinaryRead.U32(rebuilt, at);
            if (Math.Abs(left - right) > 2)
            {
                return false;
            }
        }
        return true;
    }
    private static int[] Joints(IGltfSource source)
    {
        foreach (JsonElement skin in GltfJson.Array(source.Root, "skins"))
        {
            List<int> joints = [];
            foreach (JsonElement joint in GltfJson.Array(skin, "joints"))
            {
                joints.Add(joint.GetInt32());
            }
            if (joints.Count > 0)
            {
                return [.. joints];
            }
        }
        throw new InvalidDataException("the model has no skin, so it carries no skeleton to read");
    }
    private static Matrix4x4 Local(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement matrix) && matrix.GetArrayLength() == 16)
        {
            float[] values = new float[16];
            int at = 0;
            foreach (JsonElement value in matrix.EnumerateArray())
            {
                values[at++] = value.GetSingle();
            }
            return new Matrix4x4(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]);
        }
        Vector3 move = Read3(node, "translation", Vector3.Zero);
        Vector3 size = Read3(node, "scale", Vector3.One);
        Quaternion turn = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out JsonElement rotation) && rotation.GetArrayLength() == 4)
        {
            float[] values = new float[4];
            int at = 0;
            foreach (JsonElement value in rotation.EnumerateArray())
            {
                values[at++] = value.GetSingle();
            }
            turn = new Quaternion(values[0], values[1], values[2], values[3]);
        }
        return Matrix4x4.CreateScale(size)
            * Matrix4x4.CreateFromQuaternion(turn)
            * Matrix4x4.CreateTranslation(move);
    }
    private static Vector3 Read3(JsonElement node, string name, Vector3 fallback)
    {
        if (!node.TryGetProperty(name, out JsonElement array) || array.GetArrayLength() != 3)
        {
            return fallback;
        }
        float[] values = new float[3];
        int at = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            values[at++] = value.GetSingle();
        }
        return new Vector3(values[0], values[1], values[2]);
    }
    private static int[] Fixed(Matrix4x4 matrix)
    {
        float[] values =
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
        int[] fixedPoint = new int[16];
        for (int k = 0; k < 16; k++)
        {
            fixedPoint[k] = (int)MathF.Round(values[k] * 4096f);
        }
        fixedPoint[3] = 0;
        fixedPoint[7] = 0;
        fixedPoint[11] = 0;
        fixedPoint[15] = 4096;
        return fixedPoint;
    }
    private sealed class Channel
    {
        private float[] _moveTimes = [];
        private float[] _move = [];
        private float[] _turnTimes = [];
        private float[] _turn = [];
        private float[] _sizeTimes = [];
        private float[] _size = [];
        public void Set(string path, float[] times, float[] values)
        {
            switch (path)
            {
                case "translation":
                    _moveTimes = times;
                    _move = values;
                    break;
                case "rotation":
                    _turnTimes = times;
                    _turn = values;
                    break;
                case "scale":
                    _sizeTimes = times;
                    _size = values;
                    break;
                default:
                    break;
            }
        }
        public Matrix4x4 Sample(float seconds)
        {
            Vector3 move = SampleVector(_moveTimes, _move, seconds, Vector3.Zero);
            Vector3 size = SampleVector(_sizeTimes, _size, seconds, Vector3.One);
            Quaternion turn = SampleQuaternion(_turnTimes, _turn, seconds);
            return Matrix4x4.CreateScale(size)
                * Matrix4x4.CreateFromQuaternion(turn)
                * Matrix4x4.CreateTranslation(move);
        }
        private static (int Low, int High, float Blend) Span(float[] times, float seconds)
        {
            if (times.Length == 0)
            {
                return (0, 0, 0f);
            }
            for (int i = 0; i + 1 < times.Length; i++)
            {
                if (seconds <= times[i + 1])
                {
                    float span = times[i + 1] - times[i];
                    return (i, i + 1, span > 0 ? Math.Clamp((seconds - times[i]) / span, 0f, 1f) : 0f);
                }
            }
            return (times.Length - 1, times.Length - 1, 0f);
        }
        private static Vector3 SampleVector(float[] times, float[] values, float seconds, Vector3 fallback)
        {
            if (times.Length == 0 || values.Length < times.Length * 3)
            {
                return fallback;
            }
            (int low, int high, float blend) = Span(times, seconds);
            Vector3 a = new(values[low * 3], values[(low * 3) + 1], values[(low * 3) + 2]);
            Vector3 b = new(values[high * 3], values[(high * 3) + 1], values[(high * 3) + 2]);
            return Vector3.Lerp(a, b, blend);
        }
        private static Quaternion SampleQuaternion(float[] times, float[] values, float seconds)
        {
            if (times.Length == 0 || values.Length < times.Length * 4)
            {
                return Quaternion.Identity;
            }
            (int low, int high, float blend) = Span(times, seconds);
            Quaternion a = new(
                values[low * 4], values[(low * 4) + 1], values[(low * 4) + 2], values[(low * 4) + 3]);
            Quaternion b = new(
                values[high * 4], values[(high * 4) + 1], values[(high * 4) + 2], values[(high * 4) + 3]);
            return Quaternion.Normalize(Quaternion.Slerp(a, b, blend));
        }
    }
}
