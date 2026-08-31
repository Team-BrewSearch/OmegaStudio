using Omega.Plugins;
using System.Numerics;
namespace Harugeki.Formats.Math;

public static class Transforms
{
    private static ReadOnlySpan<int> MirroredElements => [1, 3, 4, 6, 9, 11, 12, 14];
    public static void Mirror(Span<float> m)
    {
        if (m.Length < 16)
        {
            throw new ArgumentException("expected a 4x4 matrix", nameof(m));
        }
        foreach (int i in MirroredElements)
        {
            m[i] = -m[i];
        }
    }
    public static Matrix4x4 Mirrored(ReadOnlySpan<float> m)
    {
        Span<float> t = stackalloc float[16];
        m[..16].CopyTo(t);
        Mirror(t);
        return MatrixOps.ToMatrix(t);
    }
}
