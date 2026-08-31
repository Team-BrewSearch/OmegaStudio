using Harugeki.Formats.Binary;
using Harugeki.Formats.Math;
namespace Harugeki.Formats;

public sealed class RingBone
{
    public const int WeightsOffset = 0x150;
    private const int BindOffset = 0x88;
    private const int WeightCountOffset = 0x84;
    private const int WeightStride = 8;
    public int NodeIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Related { get; init; }
    public int ParentNodeIndex => Related - 1;
    public int MeshIndex { get; init; }
    public float[] Bind { get; init; } = new float[16];
    public int[] WeightVertices { get; init; } = [];
    public float[] Weights { get; init; } = [];
    public static RingBone Parse(ReadOnlySpan<byte> blob, int off, int end, int index)
    {
        int count = (int)BinaryRead.U32(blob, off + WeightCountOffset);
        if (off + WeightsOffset + ((long)count * WeightStride) > end)
        {
            count = System.Math.Max(0, (end - off - WeightsOffset) / WeightStride);
        }
        float[] bind = new float[16];
        for (int i = 0; i < 16; i++)
        {
            bind[i] = BinaryRead.F32(blob, off + BindOffset + (i * 4));
        }
        Transforms.Mirror(bind);
        int[] vertices = new int[count];
        float[] weights = new float[count];
        for (int i = 0; i < count; i++)
        {
            int at = off + WeightsOffset + (i * WeightStride);
            vertices[i] = (int)BinaryRead.U32(blob, at);
            weights[i] = BinaryRead.F32(blob, at + 4);
        }
        return new RingBone
        {
            NodeIndex = index,
            Name = BinaryRead.Name(blob, off, 0x70),
            Related = (int)BinaryRead.U32(blob, off + 0x70),
            MeshIndex = (int)BinaryRead.U32(blob, off + 0x80),
            Bind = bind,
            WeightVertices = vertices,
            Weights = weights,
        };
    }
    public override string ToString()
    {
        return $"{Name} weights={Weights.Length}";
    }
}
