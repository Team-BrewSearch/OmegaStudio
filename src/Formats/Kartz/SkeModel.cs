using Kartz.Formats.Binary;
using System.Buffers.Binary;
using System.Numerics;
namespace Kartz.Formats;

public sealed class SkeBone
{
    public const int Size = 68;
    public const int WideSize = 132;
    public required int Parent { get; init; }
    public required int[] Matrix { get; init; }
    public int[] Upward { get; init; } = [];
    public float Translation(int axis)
    {
        return Matrix[12 + axis] / 4096f;
    }
    public float Row(int row, int column)
    {
        return Matrix[(row * 4) + column] / 4096f;
    }
}
public sealed class SkeModel
{
    public const int HeaderSize = 8;
    private SkeModel(IReadOnlyList<SkeBone> bones, int record)
    {
        Bones = bones;
        RecordSize = record;
    }
    public IReadOnlyList<SkeBone> Bones { get; }
    public int RecordSize { get; }
    public bool BonesArePlaced => RecordSize == SkeBone.WideSize;
    public static bool IsSkeleton(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _);
    }
    public static SkeModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out SkeModel? model) && model is not null
            ? model
            : throw new InvalidDataException("not a Kartz .ske skeleton");
    }
    public byte[] Serialize()
    {
        byte[] output = new byte[HeaderSize + (Bones.Count * RecordSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)Bones.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), HeaderSize);
        for (int i = 0; i < Bones.Count; i++)
        {
            int at = HeaderSize + (i * RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), Bones[i].Parent);
            for (int k = 0; k < 16; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(at + 4 + (k * 4)), Bones[i].Matrix[k]);
            }
            for (int k = 0; k < Bones[i].Upward.Length && RecordSize == SkeBone.WideSize; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(at + 68 + (k * 4)), Bones[i].Upward[k]);
            }
        }
        return output;
    }
    public static Matrix4x4 Upright { get; } = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);
    public static Matrix4x4 Downright { get; } = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);
    private static readonly double[] s_upright =
    [
        Upright.M11, Upright.M12, Upright.M13, Upright.M14,
        Upright.M21, Upright.M22, Upright.M23, Upright.M24,
        Upright.M31, Upright.M32, Upright.M33, Upright.M34,
        Upright.M41, Upright.M42, Upright.M43, Upright.M44,
    ];
    public float[] RestMatrix(int bone)
    {
        return Single(Multiply(World(bone), s_upright));
    }
    public static float[] Pose(double[] animated)
    {
        ArgumentNullException.ThrowIfNull(animated);
        return Single(Multiply(animated, s_upright));
    }
    private static float[] Single(double[] matrix)
    {
        float[] result = new float[16];
        for (int k = 0; k < 16; k++)
        {
            result[k] = (float)matrix[k];
        }
        return result;
    }
    public double[] Rest(int bone)
    {
        return Multiply(World(bone), s_upright);
    }
    public double[] World(int bone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bone);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bone, Bones.Count);
        double[] world = Local(bone);
        if (BonesArePlaced)
        {
            return world;
        }
        for (int parent = Bones[bone].Parent; parent >= 0; parent = Bones[parent].Parent)
        {
            world = Multiply(world, Local(parent));
        }
        return world;
    }
    private double[] Local(int bone)
    {
        double[] local = new double[16];
        for (int k = 0; k < 16; k++)
        {
            local[k] = Bones[bone].Matrix[k] / 4096.0;
        }
        return local;
    }
    private static double[] Multiply(double[] child, double[] parent)
    {
        double[] product = new double[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += child[(row * 4) + k] * parent[(k * 4) + column];
                }
                product[(row * 4) + column] = sum;
            }
        }
        return product;
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, out SkeModel? model)
    {
        return TryRead(blob, SkeBone.Size, out model) || TryRead(blob, SkeBone.WideSize, out model);
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, int record, out SkeModel? model)
    {
        model = null;
        if (blob.Length < HeaderSize + record)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, 0);
        if (count == 0 || count > 1024
            || BinaryRead.U32(blob, 4) != HeaderSize
            || blob.Length != HeaderSize + (count * record))
        {
            return false;
        }
        List<SkeBone> bones = new((int)count);
        for (int i = 0; i < count; i++)
        {
            int at = HeaderSize + (i * record);
            int parent = (int)BinaryRead.U32(blob, at);
            if (parent < -1 || parent >= i)
            {
                return false;
            }
            int[] matrix = new int[16];
            for (int k = 0; k < 16; k++)
            {
                matrix[k] = (int)BinaryRead.U32(blob, at + 4 + (k * 4));
            }
            if (matrix[3] != 0 || matrix[7] != 0 || matrix[11] != 0 || matrix[15] != 4096)
            {
                return false;
            }
            int[] upward = [];
            if (record == SkeBone.WideSize)
            {
                upward = new int[16];
                for (int k = 0; k < 16; k++)
                {
                    upward[k] = (int)BinaryRead.U32(blob, at + 68 + (k * 4));
                }
                if (upward[3] != 0 || upward[7] != 0 || upward[11] != 0 || upward[15] != 4096)
                {
                    return false;
                }
            }
            bones.Add(new SkeBone { Parent = parent, Matrix = matrix, Upward = upward });
        }
        model = new SkeModel(bones, record);
        return true;
    }
}
