using Kartz.Formats.Binary;
using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// A 5TX texture (NTTX/PALT/IMGE format) decoded to RGBA8.
///
/// <code>
/// [NTTX header 16 bytes]
/// [PALT section: "PALT" u32 size u32 numColors u16[numColors] RGB555]
/// [IMGE section: "IMGE" u32 size  u8 fmt  u8 log2Width  u8 log2Height  u8 c0xp
///                u16 origWidth  u16 origHeight  u32 pixSize  u8[] texel (+cmp for fmt 5)]
/// </code>
///
/// This is NitroPaint's "iStudio" NDS texture container (<c>TxReadIStudio</c> in
/// <c>NitroPaint/texture.c</c>) — decoding here is a 1:1 port of that reader, including
/// its RGB555 rounding tables (<c>color.c</c>, see <see cref="DsColor"/>).
///
/// Formats: 1=A3I5, 2=2bpp/4-color palette, 3=4bpp/16-color palette, 4=8bpp/256-color
/// palette, 5=4×4 block compressed, 6=A5I3, 7=direct RGB555 (bit15=opaque).
///
/// <para>
/// Color-0 transparency (<c>c0xp</c>, payload+0x3) is real but barely used: 1 texture of 1422
/// in Super Star Kartz and 5 of 1045 in Madagascar Kartz set it. What actually drives
/// foliage/chain-link cutouts here is a colorkey convention layered on top of the format: any
/// pixel that decodes to pure magenta (255,0,255) is forced fully transparent, applied
/// uniformly after format decode regardless of format — see the pass at the end of
/// <see cref="Parse(ReadOnlySpan{byte}, string)"/>.
/// </para>
///
/// <para>
/// Track geometry does not honour <c>c0xp</c> at all, which is why
/// <see cref="ParseForTrack"/> exists — see its remarks.
/// </para>
/// </summary>
public sealed class SskTexture
{
    private static ReadOnlySpan<byte> NttxMagic => "NTTX"u8;
    private static ReadOnlySpan<byte> PaltMagic => "PALT"u8;
    private static ReadOnlySpan<byte> ImgeMagic => "IMGE"u8;

    internal SskTexture() { }

    public string Name { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>RGBA8 pixels, top-down left-right.</summary>
    public byte[] Pixels { get; private set; } = [];

    public int Format { get; private set; }

    /// <summary>The real IMGE <c>c0xp</c> bit: palette index 0 is transparent when set.</summary>
    public bool ColorZeroTransparent { get; private set; }

    /// <summary>
    /// NTTX header bytes +0x0C/+0x0E, verbatim. Neither NitroPaint's reader nor this
    /// project's own decoder ever reads them, but every real texture in the shipped archive
    /// but one has <c>TextureParam == 0x0010</c> — the real game engine almost certainly
    /// validates them, so a re-encode preserves whatever the texture being replaced had
    /// rather than zeroing them, the way <see cref="ColorZeroTransparent"/> already is.
    /// </summary>
    public ushort TextureParam { get; private set; }

    /// <summary>See <see cref="TextureParam"/>.</summary>
    public ushort FormatHint { get; private set; }

    /// <summary>True if any decoded pixel is fully transparent (alpha 0), from any source.</summary>
    public bool HasTransparency { get; private set; }

    public static bool IsTexture(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 16 && blob[..4].SequenceEqual(NttxMagic);
    }

    /// <summary>
    /// Reads just the IMGE dimensions, skipping the pixel decode.
    /// </summary>
    /// <remarks>
    /// The slot solver needs the size of every candidate texture in a track — up to ~68 of
    /// them — but none of their pixels. Returns false instead of throwing, because this runs
    /// over unvalidated archive entries.
    /// </remarks>
    public static bool TryReadDimensions(ReadOnlySpan<byte> blob, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!IsTexture(blob))
        {
            return false;
        }

        int secOff = 0x10;
        while (secOff + 8 <= blob.Length && blob.Slice(secOff, 4).SequenceEqual(PaltMagic))
        {
            uint paltSize = BinaryRead.U32(blob, secOff + 4);

            // Widened: the section size is file data, and secOff + paltSize wraps to a negative
            // int that the loop condition then happily accepts.
            if (paltSize == 0 || secOff + (long)paltSize > blob.Length)
            {
                return false;
            }

            secOff += (int)paltSize;
        }

        if (secOff + 20 > blob.Length || !blob.Slice(secOff, 4).SequenceEqual(ImgeMagic))
        {
            return false;
        }

        width = BinaryRead.U16(blob, secOff + 12);
        height = BinaryRead.U16(blob, secOff + 14);
        return width > 0 && height > 0;
    }

    public static SskTexture Parse(ReadOnlySpan<byte> blob, string name)
    {
        return Parse(blob, name, colorZeroTransparency: true);
    }

    /// <summary>
    /// Decodes a texture the way track geometry draws it: with color-0 transparency forced off.
    /// </summary>
    /// <remarks>
    /// On hardware, color-0 transparency is bit 29 of <c>TEXIMAGE_PARAM</c>, not a property of
    /// the texture file — and every one of the 9145 textured groups across both games' tracks
    /// (4792 in Super Star Kartz, 4353 in Madagascar Kartz) carries either <c>0x40000000</c> or
    /// <c>0x00000000</c>, so bit 29 is clear everywhere without exception. A track therefore
    /// draws palette index 0 opaque no matter what the file's <c>c0xp</c> says. All five of
    /// Madagascar's <c>c0xp</c> textures are water or lava a track does draw — <c>NY_water</c>,
    /// <c>WH_river</c>, <c>VO_lava_islands</c> — whose palettes are four near-identical blues
    /// with nothing keyed out, so honouring the flag punched holes through them.
    /// </remarks>
    public static SskTexture ParseForTrack(ReadOnlySpan<byte> blob, string name)
    {
        return Parse(blob, name, colorZeroTransparency: false);
    }

    private static SskTexture Parse(ReadOnlySpan<byte> blob, string name, bool colorZeroTransparency)
    {
        if (!IsTexture(blob))
        {
            throw new ArgumentException("not a 5TX blob", nameof(blob));
        }

        ushort textureParam = BinaryRead.U16(blob, 0x0C);
        ushort formatHint = BinaryRead.U16(blob, 0x0E);

        // Parse PALT section(s) starting at 0x10
        List<(byte R, byte G, byte B)> palette = [];
        int secOff = 0x10;

        while (secOff + 8 <= blob.Length && blob.Slice(secOff, 4).SequenceEqual(PaltMagic))
        {
            uint paltSize = BinaryRead.U32(blob, secOff + 4);
            uint numColors = BinaryRead.U32(blob, secOff + 8);

            // See TryReadDimensions: a zero size never advances, and an oversized one wraps.
            if (paltSize == 0 || secOff + (long)paltSize > blob.Length)
            {
                throw new ArgumentException("malformed PALT section", nameof(blob));
            }

            for (uint ci = 0; ci < numColors && secOff + 12 + ((ci + 1) * 2) <= blob.Length; ci++)
            {
                ushort c = BinaryRead.U16(blob, (int)(secOff + 12 + (ci * 2)));
                palette.Add(DsColor.FromRgb555(c));
            }

            secOff += (int)paltSize;
        }

        // IMGE section
        if (secOff + 20 > blob.Length || !blob.Slice(secOff, 4).SequenceEqual(ImgeMagic))
        {
            throw new ArgumentException("missing IMGE section", nameof(blob));
        }

        int fmt = blob[secOff + 8];
        int log2Width = blob[secOff + 9];
        int log2Height = blob[secOff + 10];
        bool c0xp = blob[secOff + 11] != 0;
        int origWidth = BinaryRead.U16(blob, secOff + 12);
        int origHeight = BinaryRead.U16(blob, secOff + 14);
        int pixDataOff = secOff + 20;

        // Source texel/cmp arrays are always sized to the *padded* power-of-two dimensions
        // (NitroPaint TxReadIStudio computes texelSize from 8<<log2Width / 8<<log2Height, not
        // origWidth/origHeight), even though only origWidth x origHeight pixels are rendered.
        int paddedWidth = 8 << SMath.Clamp(log2Width, 0, 10);
        int paddedHeight = 8 << SMath.Clamp(log2Height, 0, 10);

        (byte[] pixels, bool hasTransparency) = Decode(
            blob, pixDataOff, paddedWidth, paddedHeight, origWidth, origHeight, fmt, palette,
            c0xp && colorZeroTransparency);

        ApplyColorKey(pixels, ref hasTransparency);

        return new SskTexture
        {
            Name = name,
            Width = origWidth,
            Height = origHeight,
            Pixels = pixels,
            Format = fmt,
            ColorZeroTransparent = c0xp,
            HasTransparency = hasTransparency,
            TextureParam = textureParam,
            FormatHint = formatHint,
        };
    }

    /// <summary>Any pixel that decodes to pure magenta becomes fully transparent.</summary>
    /// <remarks>
    /// This game's colorkey convention, not part of the DS GPU's own format semantics — see
    /// the class doc comment. Applied once, uniformly, after every format's own decode.
    /// </remarks>
    private static void ApplyColorKey(byte[] pixels, ref bool hasTransparency)
    {
        bool any = hasTransparency;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 255 && pixels[i + 1] == 0 && pixels[i + 2] == 255)
            {
                pixels[i + 3] = 0;
                any = true;
            }
        }

        hasTransparency = any;
    }

    private static (byte[] Pixels, bool HasTransparency) Decode(
        ReadOnlySpan<byte> blob, int dataOff, int paddedWidth, int paddedHeight,
        int origWidth, int origHeight, int fmt,
        List<(byte R, byte G, byte B)> palette, bool c0xp)
    {
        byte[] pixels = new byte[SMath.Max(0, origWidth) * SMath.Max(0, origHeight) * 4];
        if (origWidth <= 0 || origHeight <= 0)
        {
            return (pixels, false);
        }

        int available = SMath.Max(0, blob.Length - dataOff);
        int texelSize = SMath.Clamp(DsColor.CalcTexelSize(fmt, paddedWidth, paddedHeight), 0, available);
        ReadOnlySpan<byte> texel = blob.Slice(dataOff, texelSize);

        ReadOnlySpan<byte> cmp = default;
        if (fmt == 5)
        {
            int cmpAvailable = SMath.Max(0, blob.Length - dataOff - texelSize);
            int cmpSize = SMath.Clamp(texelSize / 2, 0, cmpAvailable);
            cmp = blob.Slice(dataOff + texelSize, cmpSize);
        }

        bool hasTransparency = false;
        for (int y = 0; y < origHeight; y++)
        {
            for (int x = 0; x < origWidth; x++)
            {
                (byte r, byte g, byte b, byte a) = SamplePixel(fmt, texel, cmp, paddedWidth, x, y, palette, c0xp);
                int outIdx = ((y * origWidth) + x) * 4;
                pixels[outIdx] = r;
                pixels[outIdx + 1] = g;
                pixels[outIdx + 2] = b;
                pixels[outIdx + 3] = a;
                hasTransparency |= a == 0;
            }
        }

        return (pixels, hasTransparency);
    }

    /// <summary>Samples one pixel. Mirrors NitroPaint's per-format <c>TxiSample*</c> family exactly.</summary>
    private static (byte R, byte G, byte B, byte A) SamplePixel(
        int fmt, ReadOnlySpan<byte> texel, ReadOnlySpan<byte> cmp, int paddedWidth,
        int x, int y, List<(byte R, byte G, byte B)> palette, bool c0xp)
    {
        int iPx = x + (y * paddedWidth);
        switch (fmt)
        {
            case 2: // PLTT4 — 2bpp, 4 colors/byte... 4 pixels/byte
                return SamplePalette(palette, (SafeByte(texel, iPx >> 2) >> ((iPx & 3) * 2)) & 0x3, c0xp);

            case 3: // PLTT16 — 4bpp, 2 pixels/byte
                return SamplePalette(palette, (SafeByte(texel, iPx >> 1) >> ((iPx & 1) * 4)) & 0xF, c0xp);

            case 4: // PLTT256 — 8bpp, 1 pixel/byte
                return SamplePalette(palette, SafeByte(texel, iPx), c0xp);

            case 1: // A3I5 — 3-bit alpha, 5-bit index
                {
                    int d = SafeByte(texel, iPx);
                    int a3 = (d >> 5) & 0x7;
                    int a5 = (a3 << 2) | (a3 >> 1);
                    byte alpha = (byte)(((a5 * 510) + 31) / 62);
                    (byte r, byte g, byte b) = PaletteAt(palette, d & 0x1F);
                    return (r, g, b, alpha);
                }

            case 6: // A5I3 — 5-bit alpha, 3-bit index
                {
                    int d = SafeByte(texel, iPx);
                    int a5 = (d >> 3) & 0x1F;
                    byte alpha = (byte)(((a5 * 510) + 31) / 62);
                    (byte r, byte g, byte b) = PaletteAt(palette, d & 0x7);
                    return (r, g, b, alpha);
                }

            case 7: // DIRECT — 16bpp RGB555, bit15 = opaque
                {
                    int byteOff = iPx * 2;
                    ushort c = (ushort)(SafeByte(texel, byteOff) | (SafeByte(texel, byteOff + 1) << 8));
                    (byte r, byte g, byte b) = DsColor.FromRgb555(c);
                    return (r, g, b, (c & 0x8000) != 0 ? (byte)255 : (byte)0);
                }

            case 5: // TEX4x4 — 4x4 block compressed
                return SampleTex4x4(texel, cmp, paddedWidth, x, y, palette);

            default: // Unknown format — diagnostic red (not magenta: must survive the colorkey pass)
                return (255, 0, 0, 255);
        }
    }

    /// <summary>Mirrors NitroPaint's <c>TxiSampleTex4x4</c> exactly, including its four block modes.</summary>
    private static (byte R, byte G, byte B, byte A) SampleTex4x4(
        ReadOnlySpan<byte> texel, ReadOnlySpan<byte> cmp, int paddedWidth,
        int x, int y, List<(byte R, byte G, byte B)> palette)
    {
        int blocksPerRow = SMath.Max(1, paddedWidth / 4);
        int i = (x / 4) + (y / 4 * blocksPerRow);

        int texelOff = i * 4;
        uint texelWord = (uint)(SafeByte(texel, texelOff)
            | (SafeByte(texel, texelOff + 1) << 8)
            | (SafeByte(texel, texelOff + 2) << 16)
            | (SafeByte(texel, texelOff + 3) << 24));

        int cmpOff = i * 2;
        int code = SafeByte(cmp, cmpOff) | (SafeByte(cmp, cmpOff + 1) << 8);
        int address = (code & 0x3FFF) << 1;

        (byte R, byte G, byte B) c0 = PaletteAt(palette, address);
        (byte R, byte G, byte B) c1 = PaletteAt(palette, address + 1);
        (byte R, byte G, byte B) c2;
        (byte R, byte G, byte B) c3;
        bool c2Opaque;
        bool c3Opaque;

        bool interpolate = (code & 0x4000) != 0;
        bool opaqueMode = (code & 0x8000) != 0;

        if (!interpolate)
        {
            c2 = PaletteAt(palette, address + 2);
            c2Opaque = true;
            if (opaqueMode)
            {
                c3 = PaletteAt(palette, address + 3);
                c3Opaque = true;
            }
            else
            {
                c3 = default;
                c3Opaque = false;
            }
        }
        else if (opaqueMode)
        {
            c2 = DsColor.Blend(c0, c1, 3);
            c3 = DsColor.Blend(c0, c1, 5);
            c2Opaque = true;
            c3Opaque = true;
        }
        else
        {
            c2 = DsColor.Blend(c0, c1, 4);
            c3 = default;
            c2Opaque = true;
            c3Opaque = false;
        }

        int j = (x & 3) + ((y & 3) << 2);
        int pVal = (int)((texelWord >> (2 * j)) & 0x3);

        return pVal switch
        {
            0 => (c0.R, c0.G, c0.B, 255),
            1 => (c1.R, c1.G, c1.B, 255),
            2 => (c2.R, c2.G, c2.B, c2Opaque ? (byte)255 : (byte)0),
            _ => (c3.R, c3.G, c3.B, c3Opaque ? (byte)255 : (byte)0),
        };
    }

    private static (byte R, byte G, byte B, byte A) SamplePalette(
        List<(byte R, byte G, byte B)> palette, int index, bool c0xp)
    {
        (byte r, byte g, byte b) = PaletteAt(palette, index);
        byte a = (index == 0 && c0xp) ? (byte)0 : (byte)255;
        return (r, g, b, a);
    }

    private static (byte R, byte G, byte B) PaletteAt(List<(byte R, byte G, byte B)> palette, int index)
    {
        return (uint)index < (uint)palette.Count ? palette[index] : ((byte)0, (byte)0, (byte)0);
    }

    private static int SafeByte(ReadOnlySpan<byte> span, int index)
    {
        return (uint)index < (uint)span.Length ? span[index] : 0;
    }

    // No SwapRedBlue here any more: the RGBA-to-BGRA swizzle is the host's, in
    // ImageData.SwapRedBlue. See RingTexture for the same removal.

    /// <summary>
    /// Replaces the decoded pixel buffer. Used by the texture replacer after a
    /// successful re-encode so every pane showing this texture sees the new image.
    /// </summary>
    public void SetPixels(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} needs {width * height * 4} RGBA bytes", nameof(rgba));
        }

        Width = width;
        Height = height;
        Pixels = rgba;

        HasTransparency = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 0)
            {
                HasTransparency = true;
                break;
            }
        }
    }

    public override string ToString()
    {
        return $"{Name} {Width}x{Height} fmt{Format}";
    }
}
