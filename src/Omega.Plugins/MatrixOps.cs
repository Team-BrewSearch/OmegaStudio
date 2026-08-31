using System.Numerics;
namespace Omega.Plugins;
/// <summary>The matrix conversions every format and the viewport share, so row order is decided in one place.</summary>
public static class MatrixOps
{
    /// <summary>Reads a row-major 4x4 matrix out of a flat span.</summary>
    /// <param name="m">Sixteen floats, row by row.</param>
    /// <returns>The matrix.</returns>
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
    /// <summary>Writes a matrix into a flat span, row by row.</summary>
    /// <param name="m">The matrix to write.</param>
    /// <param name="destination">Where to write; needs sixteen floats from the offset.</param>
    /// <param name="at">Offset to start writing at.</param>
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
    /// <summary>Blends two transforms by decomposing them, so rotation follows the shortest arc instead of skewing through the midpoint. Falls back to a component-wise mix when either matrix will not decompose.</summary>
    /// <param name="a">The transform at t equal to zero.</param>
    /// <param name="b">The transform at t equal to one.</param>
    /// <param name="t">How far between the two, clamped at the ends.</param>
    /// <returns>The blended transform.</returns>
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
    /// <summary>Mixes two matrices element by element.</summary>
    /// <param name="a">The matrix at t equal to zero.</param>
    /// <param name="b">The matrix at t equal to one.</param>
    /// <param name="t">How far between the two.</param>
    /// <returns>The mixed matrix.</returns>
    public static Matrix4x4 Lerp(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return a + ((b - a) * t);
    }
    /// <summary>Inverts a matrix, yielding the identity when it is singular so a bad bind pose cannot produce infinities.</summary>
    /// <param name="m">The matrix to invert.</param>
    /// <returns>The inverse, or the identity.</returns>
    public static Matrix4x4 InvertOrIdentity(in Matrix4x4 m)
    {
        return Matrix4x4.Invert(m, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
    }
}
