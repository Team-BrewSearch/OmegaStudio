using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed class Imy
{
    private const int HeaderSize = 0x20;
    private const byte Stored = 1;
    private const int MaxRun = 16;
    private const int MaxLookback = 176;
    private readonly byte[] _header;
    private Imy(byte[] header, ushort[] palette, byte[] pixels)
    {
        _header = header;
        Palette = palette;
        Pixels = pixels;
    }
    public ushort[] Palette { get; }
    public byte[] Pixels { get; }
    public int RowBytes => BinaryRead.U16(_header, 8);
    public bool IsStored => (_header[10] & Stored) != 0;
    public int Bits => _header[11];
    public int Height => BinaryRead.U16(_header, 12);
    public int Width => RowBytes * 8 / Bits;
    public string Label => $"IMY {Bits}bpp, {Palette.Length} colours{(IsStored ? ", stored" : string.Empty)}";
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("IMY\0"u8);
    }
    public static Imy Read(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, HeaderSize);
        if (!Claims(data))
        {
            throw new InvalidDataException("not an IMY image");
        }
        byte[] header = data[..HeaderSize].ToArray();
        uint size = BinaryRead.U32(data, 4);
        int rowBytes = BinaryRead.U16(data, 8);
        int bits = data[11];
        int height = BinaryRead.U16(data, 12);
        int colors = BinaryRead.U16(data, 14);
        if (bits is not (4 or 8) || colors is 0 || colors > 1 << bits || rowBytes == 0 || height == 0)
        {
            throw new InvalidDataException($"unsupported IMY layout: {bits}bpp, {colors} colours, {rowBytes}x{height} bytes");
        }
        int pixelsAt = HeaderSize + (colors * 2);
        if (size != pixelsAt + ((long)rowBytes * height))
        {
            throw new InvalidDataException($"IMY size {size} does not match {rowBytes}x{height} bytes and {colors} colours");
        }
        BinaryRead.Need(data, HeaderSize, colors * 2);
        ushort[] palette = new ushort[colors];
        for (int i = 0; i < colors; i++)
        {
            palette[i] = BinaryRead.U16(data, HeaderSize + (2 * i));
        }
        int length = rowBytes * height;
        byte[] pixels;
        if ((data[10] & Stored) != 0)
        {
            BinaryRead.Need(data, pixelsAt, length);
            pixels = data.Slice(pixelsAt, length).ToArray();
        }
        else
        {
            pixels = Expand(data, pixelsAt, length, rowBytes);
        }
        return new Imy(header, palette, pixels);
    }
    public ImageData ToImage(string name)
    {
        byte[] rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            int index = IndexAt(i);
            ushort color = index < Palette.Length ? Palette[index] : (ushort)0;
            rgba[(i * 4) + 0] = Expand5(color);
            rgba[(i * 4) + 1] = Expand5(color >> 5);
            rgba[(i * 4) + 2] = Expand5(color >> 10);
            rgba[(i * 4) + 3] = (color & 0x8000) != 0 ? (byte)255 : (byte)0;
        }
        return ImageData.Create(name, Width, Height, rgba, Label);
    }
    public byte[] Encode(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width != Width || image.Height != Height)
        {
            throw new InvalidDataException($"the image is {image.Width}x{image.Height}; this IMY is {Width}x{Height}");
        }
        int count = Width * Height;
        ushort[] colors = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            colors[i] = Pack(image.Rgba.AsSpan(i * 4, 4));
        }
        ushort[] palette = Fits(colors, Palette) ? Palette : NewPalette(colors);
        Dictionary<ushort, byte> lookup = [];
        int clear = -1;
        for (int i = palette.Length - 1; i >= 0; i--)
        {
            lookup[palette[i]] = (byte)i;
            clear = (palette[i] & 0x8000) == 0 ? i : clear;
        }
        byte[] pixels = new byte[Pixels.Length];
        for (int i = 0; i < count; i++)
        {
            int kept = IndexAt(i);
            byte index = palette == Palette && kept < palette.Length && palette[kept] == colors[i] ? (byte)kept
                : lookup.TryGetValue(colors[i], out byte exact) ? exact : (byte)clear;
            pixels[Bits == 8 ? i : i >> 1] |= (byte)(Bits == 8 ? index : index << ((i & 1) * 4));
        }
        byte[]? packed = IsStored ? null : Shrink(pixels, RowBytes);
        byte[] output = new byte[HeaderSize + (palette.Length * 2) + (packed is null ? pixels.Length : (packed.Length + 3) & ~3)];
        _header.CopyTo(output, 0);
        output[10] = (byte)(packed is null ? _header[10] | Stored : _header[10] & ~Stored);
        for (int i = 0; i < palette.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(HeaderSize + (2 * i)), palette[i]);
        }
        (packed ?? pixels).CopyTo(output, HeaderSize + (palette.Length * 2));
        return output;
    }
    private int IndexAt(int pixel)
    {
        return Bits == 8 ? Pixels[pixel] : Pixels[pixel >> 1] >> ((pixel & 1) * 4) & 0xF;
    }
    private static byte[] Expand(ReadOnlySpan<byte> data, int at, int length, int rowBytes)
    {
        BinaryRead.Need(data, at, 2);
        int infoAt = at + 2;
        int infoEnd = infoAt + BinaryRead.U16(data, at);
        int source = infoEnd;
        ReadOnlySpan<int> lookback = [2, rowBytes, rowBytes + 2, rowBytes - 2];
        byte[] output = new byte[length + (MaxRun * 2)];
        int written = 0;
        while (written < length)
        {
            if (infoAt >= infoEnd)
            {
                throw new InvalidDataException($"IMY data ends after {written} of {length} bytes");
            }
            BinaryRead.Need(data, infoAt, 1);
            byte info = data[infoAt++];
            if (info >= 0xC0)
            {
                int back = lookback[(info >> 4) & 3];
                if (back <= 0 || back > written)
                {
                    throw new InvalidDataException($"IMY run at 0x{written:X} looks back {back} bytes");
                }
                for (int i = 0; i <= (info & 0xF); i++, written += 2)
                {
                    output[written] = output[written - back];
                    output[written + 1] = output[written + 1 - back];
                }
            }
            else if (info >= 0x10)
            {
                int from = source - (((info - 0x10) * 2) + 2);
                if (from < infoEnd)
                {
                    throw new InvalidDataException($"IMY repeat at 0x{written:X} reaches before its data");
                }
                data.Slice(from, 2).CopyTo(output.AsSpan(written));
                written += 2;
            }
            else
            {
                int bytes = (info + 1) * 2;
                BinaryRead.Need(data, source, bytes);
                data.Slice(source, bytes).CopyTo(output.AsSpan(written));
                source += bytes;
                written += bytes;
            }
        }
        return output[..length];
    }
    private static byte[]? Shrink(byte[] pixels, int rowBytes)
    {
        if (pixels.Length % 2 != 0)
        {
            return null;
        }
        int[] lookback = [2, rowBytes, rowBytes + 2, rowBytes - 2];
        List<byte> info = [];
        List<byte> source = [];
        int literals = 0;
        int position = 0;
        while (position < pixels.Length)
        {
            (int run, int which) = LongestRun(pixels, position, lookback);
            int repeat = run == 0 ? Repeat(pixels, position, source) : -1;
            if (run == 0 && repeat < 0)
            {
                if (literals == 0)
                {
                    info.Add(0);
                }
                info[^1] = (byte)literals;
                source.Add(pixels[position]);
                source.Add(pixels[position + 1]);
                literals = literals + 1 == MaxRun ? 0 : literals + 1;
                position += 2;
                continue;
            }
            literals = 0;
            info.Add(run > 0 ? (byte)(0xC0 | (which << 4) | (run - 1)) : (byte)(0x10 + repeat));
            position += run > 0 ? run * 2 : 2;
        }
        if (info.Count > ushort.MaxValue)
        {
            return null;
        }
        byte[] output = new byte[2 + info.Count + source.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)info.Count);
        info.CopyTo(output, 2);
        source.CopyTo(output, 2 + info.Count);
        return output.Length < pixels.Length ? output : null;
    }
    private static (int Run, int Which) LongestRun(byte[] pixels, int position, int[] lookback)
    {
        int best = 0;
        int which = 0;
        for (int k = 0; k < lookback.Length; k++)
        {
            int back = lookback[k];
            if (back <= 0 || back % 2 != 0 || back > position)
            {
                continue;
            }
            int run = 0;
            while (run < MaxRun && position + (run * 2) + 1 < pixels.Length
                && pixels[position + (run * 2)] == pixels[position + (run * 2) - back]
                && pixels[position + (run * 2) + 1] == pixels[position + (run * 2) + 1 - back])
            {
                run++;
            }
            if (run > best)
            {
                best = run;
                which = k;
            }
        }
        return (best, which);
    }
    private static int Repeat(byte[] pixels, int position, List<byte> source)
    {
        for (int n = 0; n < MaxLookback && (n * 2) + 2 <= source.Count; n++)
        {
            int at = source.Count - (n * 2) - 2;
            if (source[at] == pixels[position] && source[at + 1] == pixels[position + 1])
            {
                return n;
            }
        }
        return -1;
    }
    private static bool Fits(ushort[] colors, ushort[] palette)
    {
        HashSet<ushort> known = [.. palette];
        bool hasClear = palette.Any(c => (c & 0x8000) == 0);
        return colors.All(c => known.Contains(c) || ((c & 0x8000) == 0 && hasClear));
    }
    private ushort[] NewPalette(ushort[] colors)
    {
        ushort[] distinct = [.. colors.Where(c => (c & 0x8000) == 0).Take(1).Concat(colors.Where(c => (c & 0x8000) != 0)).Distinct()];
        if (distinct.Length > Palette.Length)
        {
            throw new InvalidDataException($"the image uses {distinct.Length} colours; this IMY holds {Palette.Length}");
        }
        ushort[] palette = new ushort[Palette.Length];
        distinct.CopyTo(palette, 0);
        return palette;
    }
    private static ushort Pack(ReadOnlySpan<byte> rgba)
    {
        int color = Reduce(rgba[0]) | (Reduce(rgba[1]) << 5) | (Reduce(rgba[2]) << 10);
        return (ushort)(rgba[3] >= 128 ? color | 0x8000 : color);
    }
    private static int Reduce(byte channel)
    {
        return ((channel * 31) + 127) / 255;
    }
    private static byte Expand5(int value)
    {
        return (byte)((value & 31) * 255 / 31);
    }
}
