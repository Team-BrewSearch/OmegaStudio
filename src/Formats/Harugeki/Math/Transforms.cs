using Omega.Plugins;
using System.Numerics;

namespace Harugeki.Formats.Math;

/// <summary>
/// The one coordinate conversion this format needs.
///
/// <para>
/// Everything generic — reading a <c>float[16]</c> as a matrix, blending two keys,
/// inverting safely — lives in <see cref="MatrixOps"/> in the plugin contract, so
/// the host and every plugin share it. What is left here is the part that is
/// genuinely about this game.
/// </para>
/// </summary>
public static class Transforms
{
    /// <summary>
    /// Flat row-major indices whose sign flips under the X/Z mirror: exactly the
    /// elements (i, j) where one — and only one — of i and j is a mirrored axis.
    /// </summary>
    private static ReadOnlySpan<int> MirroredElements => [1, 3, 4, 6, 9, 11, 12, 14];

    /// <summary>
    /// Mirrors a row-major 4x4 transform in place so it describes the same motion
    /// after vertex positions have had their X and Z negated.
    ///
    /// <para>
    /// This is a <b>conjugation</b> by <c>S = diag(-1, 1, -1, 1)</c>, not a plain row
    /// or column negation: the mirrored matrix is <c>S·M·S</c>, so element (i, j)
    /// flips exactly when one — and only one — of i and j is a mirrored axis.
    /// Conjugation is what makes the mirror a homomorphism, i.e.
    /// <c>mirror(A)·mirror(B) == mirror(A·B)</c> and
    /// <c>mirror(A)⁻¹ == mirror(A⁻¹)</c>. Anything else breaks the moment two
    /// mirrored matrices are composed, which is exactly what skinning does when it
    /// builds <c>inverseBind · animatedWorld</c>.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Mirrors a raw file matrix and returns it as a <see cref="Matrix4x4"/>, leaving
    /// the source untouched. Animation keys are world-space transforms in raw file
    /// coordinates, so they need the same mirror the bind matrices got — and by the
    /// same conjugation, or <c>inverseBind · animatedWorld</c> stops being a valid
    /// composition and every bone lands somewhere wrong.
    /// </summary>
    public static Matrix4x4 Mirrored(ReadOnlySpan<float> m)
    {
        Span<float> t = stackalloc float[16];
        m[..16].CopyTo(t);
        Mirror(t);
        return MatrixOps.ToMatrix(t);
    }
}
