namespace Kartz.Formats;

/// <summary>
/// DS GPU color conversion and texture-size math, ported 1:1 from NitroPaint's
/// <c>color.c</c> / <c>texture.c</c> (functions <c>ColorConvertFromDS</c>,
/// <c>ColorConvertToDS</c>, <c>ColorRoundToDS18</c>, <c>TxRoundTextureSize</c>,
/// <c>TxCalcTexelSize</c>). Shared by <see cref="SskTexture"/> (decode) and
/// <see cref="SskTextureEncoder"/> (encode) so both sides of the round trip agree
/// on the exact same rounding.
/// </summary>
internal static class DsColor
{
    /// <summary>5-bit DS channel → 8-bit RGB channel (rounding table, not bit replication).</summary>
    private static readonly byte[] Lookup5To8 =
    [
        0, 8, 16, 25, 33, 41, 49, 58, 66, 74, 82, 90, 99, 107, 115, 123,
        132, 140, 148, 156, 165, 173, 181, 189, 197, 206, 214, 222, 230, 239, 247, 255,
    ];

    /// <summary>8-bit RGB channel → 5-bit DS channel (nearest-rounding table, not truncation).</summary>
    private static readonly byte[] Lookup8To5 =
    [
        0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2,
        2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6,
        6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8,
        8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 10,
        10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 12,
        12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13, 13, 13,
        14, 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15,
        16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17,
        18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19,
        19, 20, 20, 20, 20, 20, 20, 20, 20, 21, 21, 21, 21, 21, 21, 21,
        21, 22, 22, 22, 22, 22, 22, 22, 22, 22, 23, 23, 23, 23, 23, 23,
        23, 23, 24, 24, 24, 24, 24, 24, 24, 24, 25, 25, 25, 25, 25, 25,
        25, 25, 26, 26, 26, 26, 26, 26, 26, 26, 27, 27, 27, 27, 27, 27,
        27, 27, 27, 28, 28, 28, 28, 28, 28, 28, 28, 29, 29, 29, 29, 29,
        29, 29, 29, 30, 30, 30, 30, 30, 30, 30, 30, 31, 31, 31, 31, 31,
    ];

    /// <summary>8-bit channel → nearest 6-bit-representable 8-bit value (DS blend rounding).</summary>
    private static readonly byte[] Lookup8To6To8 =
    [
        0, 0, 0, 4, 4, 4, 4, 8, 8, 8, 8, 12, 12, 12, 12, 16,
        16, 16, 16, 20, 20, 20, 20, 24, 24, 24, 24, 28, 28, 28, 28, 32,
        32, 32, 32, 36, 36, 36, 36, 40, 40, 40, 40, 45, 45, 45, 45, 49,
        49, 49, 49, 53, 53, 53, 53, 57, 57, 57, 57, 61, 61, 61, 61, 65,
        65, 65, 65, 69, 69, 69, 69, 73, 73, 73, 73, 77, 77, 77, 77, 81,
        81, 81, 81, 85, 85, 85, 85, 85, 89, 89, 89, 89, 93, 93, 93, 93,
        97, 97, 97, 97, 101, 101, 101, 101, 105, 105, 105, 105, 109, 109, 109, 109,
        113, 113, 113, 113, 117, 117, 117, 117, 121, 121, 121, 121, 125, 125, 125, 125,
        130, 130, 130, 130, 134, 134, 134, 134, 138, 138, 138, 138, 142, 142, 142, 142,
        146, 146, 146, 146, 150, 150, 150, 150, 154, 154, 154, 154, 158, 158, 158, 158,
        162, 162, 162, 162, 166, 166, 166, 166, 170, 170, 170, 170, 170, 174, 174, 174,
        174, 178, 178, 178, 178, 182, 182, 182, 182, 186, 186, 186, 186, 190, 190, 190,
        190, 194, 194, 194, 194, 198, 198, 198, 198, 202, 202, 202, 202, 206, 206, 206,
        206, 210, 210, 210, 210, 215, 215, 215, 215, 219, 219, 219, 219, 223, 223, 223,
        223, 227, 227, 227, 227, 231, 231, 231, 231, 235, 235, 235, 235, 239, 239, 239,
        239, 243, 243, 243, 243, 247, 247, 247, 247, 251, 251, 251, 251, 255, 255, 255,
    ];

    /// <summary>RGB555 (bit15 unused) → RGB888. NitroPaint's <c>ColorConvertFromDS</c>.</summary>
    public static (byte R, byte G, byte B) FromRgb555(ushort c)
    {
        return (Lookup5To8[c & 0x1F], Lookup5To8[(c >> 5) & 0x1F], Lookup5To8[(c >> 10) & 0x1F]);
    }

    /// <summary>RGB888 → RGB555. NitroPaint's <c>ColorConvertToDS</c>.</summary>
    public static ushort ToRgb555(byte r, byte g, byte b)
    {
        return (ushort)(Lookup8To5[r] | (Lookup8To5[g] << 5) | (Lookup8To5[b] << 10));
    }

    /// <summary>
    /// Weighted blend of two RGB888 colors then rounded through the DS's 6-bit
    /// intermediate precision. NitroPaint's <c>TxiBlend</c> — used only for TEX4x4
    /// interpolated block colors. <paramref name="factor"/> is out of 8.
    /// </summary>
    public static (byte R, byte G, byte B) Blend((byte R, byte G, byte B) c1, (byte R, byte G, byte B) c2, int factor)
    {
        int r = ((c1.R * (8 - factor)) + (c2.R * factor) + 4) / 8;
        int g = ((c1.G * (8 - factor)) + (c2.G * factor) + 4) / 8;
        int b = ((c1.B * (8 - factor)) + (c2.B * factor) + 4) / 8;
        return (Lookup8To6To8[r], Lookup8To6To8[g], Lookup8To6To8[b]);
    }

    /// <summary>
    /// Rounds a dimension up to the nearest texture-legal power of two (min 8).
    /// NitroPaint's <c>TxRoundTextureSize</c>.
    /// </summary>
    public static int RoundTextureSize(int dimension)
    {
        if (dimension < 8)
        {
            return 8;
        }

        dimension = (dimension << 1) - 1;
        return 1 << Log2Floor(dimension);
    }

    /// <summary>floor(log2(x)) for x &gt; 0. NitroPaint's <c>ilog2</c>.</summary>
    public static int Log2Floor(int x)
    {
        int n = 0;
        while (x > 0)
        {
            x >>= 1;
            n++;
        }

        return n - 1;
    }

    /// <summary>Bits per texel for each IMGE format (index = format 0-7). NitroPaint's <c>bpps</c>.</summary>
    private static readonly int[] BitsPerPixel = [0, 8, 2, 4, 8, 2, 8, 16];

    /// <summary>
    /// Texel-array byte size for a format at the given (already block/power-of-two-aligned)
    /// dimensions. NitroPaint's <c>TxCalcTexelSize</c>. For format 5 (TEX4x4) the palette-index
    /// ("cmp") array is exactly half this.
    /// </summary>
    public static int CalcTexelSize(int format, int width, int height)
    {
        // The format is a whole byte out of an IMGE header, so it covers 256 values rather than
        // the eight this table has. An unknown one has no texel size; SskTexture's sampler
        // already answers it with the diagnostic red, so zero here is the consistent answer
        // rather than an array bound thrown from inside the decoder.
        if (format < 0 || format >= BitsPerPixel.Length)
        {
            return 0;
        }

        if (format == 5)
        {
            width = (width + 3) & ~3;
            height = (height + 3) & ~3;
        }

        long size = ((long)width * height * BitsPerPixel[format]) >> 3;
        return size is < 0 or > int.MaxValue ? 0 : (int)size;
    }
}
