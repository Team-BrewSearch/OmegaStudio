using Virtuos.Formats.Binary;
using SMath = System.Math;
namespace Virtuos.Formats;

public sealed class SskTexture
{
    private static ReadOnlySpan<byte> NttxMagic => "NTTX"u8;
    private static ReadOnlySpan<byte> PaltMagic => "PALT"u8;
    private static ReadOnlySpan<byte> ImgeMagic => "IMGE"u8;
    internal SskTexture() { }
    public string Name { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    public int Format { get; private set; }
    public bool ColorZeroTransparent { get; private set; }
    public ushort TextureParam { get; private set; }
    public ushort FormatHint { get; private set; }
    public bool HasTransparency { get; private set; }
    public static bool IsTexture(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 16 && blob[..4].SequenceEqual(NttxMagic);
    }
    public static SskTexture Parse(ReadOnlySpan<byte> blob, string name)
    {
        return Parse(blob, name, colorZeroTransparency: true);
    }
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
        List<(byte R, byte G, byte B)> palette = [];
        int secOff = 0x10;
        while (secOff + 8 <= blob.Length && blob.Slice(secOff, 4).SequenceEqual(PaltMagic))
        {
            uint paltSize = BinaryRead.U32(blob, secOff + 4);
            uint numColors = BinaryRead.U32(blob, secOff + 8);
            if (paltSize == 0 || secOff + paltSize > blob.Length)
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
    private static (byte R, byte G, byte B, byte A) SamplePixel(
        int fmt, ReadOnlySpan<byte> texel, ReadOnlySpan<byte> cmp, int paddedWidth,
        int x, int y, List<(byte R, byte G, byte B)> palette, bool c0xp)
    {
        int iPx = x + (y * paddedWidth);
        switch (fmt)
        {
            case 2:
                return SamplePalette(palette, (SafeByte(texel, iPx >> 2) >> ((iPx & 3) * 2)) & 0x3, c0xp);
            case 3:
                return SamplePalette(palette, (SafeByte(texel, iPx >> 1) >> ((iPx & 1) * 4)) & 0xF, c0xp);
            case 4:
                return SamplePalette(palette, SafeByte(texel, iPx), c0xp);
            case 1:
                {
                    int d = SafeByte(texel, iPx);
                    int a3 = (d >> 5) & 0x7;
                    int a5 = (a3 << 2) | (a3 >> 1);
                    byte alpha = (byte)(((a5 * 510) + 31) / 62);
                    (byte r, byte g, byte b) = PaletteAt(palette, d & 0x1F);
                    return (r, g, b, alpha);
                }
            case 6:
                {
                    int d = SafeByte(texel, iPx);
                    int a5 = (d >> 3) & 0x1F;
                    byte alpha = (byte)(((a5 * 510) + 31) / 62);
                    (byte r, byte g, byte b) = PaletteAt(palette, d & 0x7);
                    return (r, g, b, alpha);
                }
            case 7:
                {
                    int byteOff = iPx * 2;
                    ushort c = (ushort)(SafeByte(texel, byteOff) | (SafeByte(texel, byteOff + 1) << 8));
                    (byte r, byte g, byte b) = DsColor.FromRgb555(c);
                    return (r, g, b, (c & 0x8000) != 0 ? (byte)255 : (byte)0);
                }
            case 5:
                return SampleTex4x4(texel, cmp, paddedWidth, x, y, palette);
            default:
                return (255, 0, 0, 255);
        }
    }
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
