using System.Numerics;
namespace Omega.Plugins;

public static class MatrixOps
{
    public static Matrix4x4 ToMatrix(ReadOnlySpan<float> m)
    {
        return m.Length < 16
            ? throw new ArgumentException("expected a 4x4 matrix", nameof(m))
            : new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
    }
    public static void CopyTo(in Matrix4x4 m, Span<float> destination, int at = 0)
    {
        destination[at + 0] = m.M11;
        destination[at + 1] = m.M12;
        destination[at + 2] = m.M13;
        destination[at + 3] = m.M14;
        destination[at + 4] = m.M21;
        destination[at + 5] = m.M22;
        destination[at + 6] = m.M23;
        destination[at + 7] = m.M24;
        destination[at + 8] = m.M31;
        destination[at + 9] = m.M32;
        destination[at + 10] = m.M33;
        destination[at + 11] = m.M34;
        destination[at + 12] = m.M41;
        destination[at + 13] = m.M42;
        destination[at + 14] = m.M43;
        destination[at + 15] = m.M44;
    }
    public static Matrix4x4 Blend(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return t <= 0f
            ? a
            : t >= 1f
            ? b
            : !Matrix4x4.Decompose(a, out Vector3 sa, out Quaternion ra, out Vector3 ta) ||
               !Matrix4x4.Decompose(b, out Vector3 sb, out Quaternion rb, out Vector3 tb)
            ? Lerp(a, b, t)
            : Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, t))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, t))
                * Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, t));
    }
    public static Matrix4x4 Lerp(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return a + ((b - a) * t);
    }
    public static Matrix4x4 InvertOrIdentity(in Matrix4x4 m)
    {
        return Matrix4x4.Invert(m, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
    }
}
