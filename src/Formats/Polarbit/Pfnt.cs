namespace Polarbit.Formats;

/// <summary>PFNT bitmap font: a glyph table over one horizontal atlas strip (REPORT 4.3).</summary>
internal sealed class PfntFont
{
    private PfntFont()
    {
    }
    public int First { get; private init; }
    public int Last { get; private init; }
    public int Glyphs { get; private init; }
    public int LineHeight { get; private init; }
    public int Ascent { get; private init; }
    public int PixelFormat { get; private init; }
    public int Flags { get; private init; }
    public int PaletteSize { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int PixelOffset { get; private init; }
    public string Characters { get; private init; } = string.Empty;
    public string Encoding => PixelFormat == 8 ? "RGBA8888"
        : Flags == 0xDE ? "3-bit alpha over 5-bit palette index"
        : PaletteSize > 0 ? $"8-bit palette ({PaletteSize} colours)"
        : "8-bit coverage";

    public static PfntFont Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 18 && b.StartsWith("PFNT"u8) && Le.U16(b, 4) == 12, "PFNT header is truncated.");
        int count = Le.U16(b, 16);
        int p = 18 + (6 * count);
        Le.Need(p + 12 <= b.Length, "PFNT glyph table runs past the data.");
        char[] characters = new char[count];
        for (int i = 0; i < count; i++)
        {
            characters[i] = (char)Le.U16(b, 18 + (2 * i));
        }
        int format = b[p], flags = b[p + 1], palette = Le.U16(b, p + 2), width = Le.U16(b, p + 4), height = Le.U16(b, p + 6);
        Le.Need(Le.U32(b, p + 8) == 0 && format is 3 or 8, $"PFNT pixel format {format} is not supported.");
        p += 12 + ((flags & 0x80) != 0 ? 5 : 0) + (3 * palette);
        Le.Need(width > 0 && height > 0 && p + (width * height * (format == 8 ? 4 : 1)) == b.Length, "PFNT pixel data does not fill the file.");
        for (int i = 0; i < count; i++)
        {
            int at = 18 + (2 * count) + (4 * i);
            Le.Need(Le.U16(b, at) + b[at + 3] <= width, $"PFNT glyph {i} lies outside the atlas.");
        }
        return new PfntFont
        {
            Ascent = b[8],
            LineHeight = b[9],
            First = Le.U16(b, 12),
            Last = Le.U16(b, 14),
            Glyphs = count,
            PixelFormat = format,
            Flags = flags,
            PaletteSize = palette,
            Width = width,
            Height = height,
            PixelOffset = p,
            Characters = new string(characters),
        };
    }

    /// <summary>256 RGBA entries for 8-bit fonts: palette colours with index 0 transparent, 3-bit alpha over a 5-bit index (flags 0xDE), or white with the byte as alpha.</summary>
    private byte[] Table(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> palette = b.Slice(PixelOffset - (3 * PaletteSize), 3 * PaletteSize);
        byte[] table = new byte[1024];
        for (int v = 0; v < 256; v++)
        {
            Span<byte> entry = table.AsSpan(v * 4, 4);
            if (Flags == 0xDE)
            {
                int k = (v & 31) * 3;
                if (k + 3 <= palette.Length)
                {
                    palette.Slice(k, 3).CopyTo(entry);
                }
                entry[3] = (byte)((v >> 5) * 255 / 7);
            }
            else if (PaletteSize > 0)
            {
                if (v < PaletteSize)
                {
                    palette.Slice(3 * v, 3).CopyTo(entry);
                }
                entry[3] = (byte)(v > 0 ? 255 : 0);
            }
            else
            {
                entry[0] = entry[1] = entry[2] = 255;
                entry[3] = (byte)v;
            }
        }
        return table;
    }

    public byte[] Decode(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> pixels = b[PixelOffset..];
        if (PixelFormat == 8)
        {
            return pixels.ToArray();
        }
        byte[] table = Table(b);
        byte[] rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            table.AsSpan(pixels[i] * 4, 4).CopyTo(rgba.AsSpan(i * 4));
        }
        return rgba;
    }

    /// <summary>The same font with new pixels. Glyph positions are fixed, so the atlas keeps its size; palette fonts need colours from their palette, and a pixel whose colour is unchanged keeps its index.</summary>
    public byte[] Encode(ReadOnlySpan<byte> b, byte[] rgba, int width, int height)
    {
        Le.Need(width == Width && height == Height,
            $"The font atlas is {Width}x{Height} and its glyph table points into it; the image must keep that size, not {width}x{height}.");
        byte[] output = b.ToArray();
        Span<byte> pixels = output.AsSpan(PixelOffset);
        if (PixelFormat == 8)
        {
            rgba.CopyTo(pixels);
            return output;
        }
        if (PaletteSize == 0 && Flags != 0xDE)
        {
            for (int i = 0; i < Width * Height; i++)
            {
                pixels[i] = rgba[(i * 4) + 3];
            }
            return output;
        }
        byte[] table = Table(b);
        Dictionary<uint, byte> lookup = [];
        int clear = -1;
        for (int v = 255; v >= 0; v--)
        {
            lookup[BitConverter.ToUInt32(table, v * 4)] = (byte)v;
            clear = table[(v * 4) + 3] == 0 ? v : clear;
        }
        for (int i = 0; i < Width * Height; i++)
        {
            uint colour = BitConverter.ToUInt32(rgba, i * 4);
            if (BitConverter.ToUInt32(table, pixels[i] * 4) == colour)
            {
                continue;
            }
            if (lookup.TryGetValue(colour, out byte index))
            {
                pixels[i] = index;
            }
            else if (rgba[(i * 4) + 3] == 0 && clear >= 0)
            {
                pixels[i] = (byte)clear;
            }
            else
            {
                throw new InvalidDataException(
                    $"Pixel ({i % Width}, {i / Width}) uses a colour that is not in the font's palette; paint with the palette's colours.");
            }
        }
        return output;
    }
}
