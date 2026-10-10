using System.Buffers.Binary;
using System.Numerics;
namespace Polarbit.Formats;

/// <summary>PowerVR legacy (v2) texture: 52-byte header, then the whole mip chain, largest level first (REPORT 4.2).</summary>
internal sealed class PvrTexture
{
    public const int HeaderSize = 52;
    private const uint Twiddled = 0x200;
    private const uint BottomUp = 0x10000;
    private PvrTexture()
    {
    }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Format { get; private init; }
    public uint Flags { get; private init; }
    public int Bpp { get; private init; }
    public uint[] Masks { get; private init; } = [];
    public (int Width, int Height, int Size)[] Levels { get; private init; } = [];
    public bool IsPvrtc => Format is 0x18 or 0x19;
    public string FormatName => Format switch
    {
        0x10 => "RGBA4444",
        0x11 => "RGBA5551",
        0x12 => "RGBA8888",
        0x13 => "RGB565",
        0x16 => "I8",
        0x17 => "AI88",
        0x18 => "PVRTC 2bpp",
        0x19 => "PVRTC 4bpp",
        _ => $"0x{Format:X2}",
    };

    public static PvrTexture Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= HeaderSize && Le.U32(b, 0) == HeaderSize && b.Slice(44, 4).SequenceEqual("PVR!"u8) && Le.U32(b, 48) == 1,
            "PVR header is not a 52-byte legacy header with one surface.");
        int height = (int)Le.U32(b, 4);
        int width = (int)Le.U32(b, 8);
        uint mips = Le.U32(b, 12);
        uint flags = Le.U32(b, 16);
        int format = (int)(flags & 0xFF);
        Le.Need(format is 0x10 or 0x11 or 0x12 or 0x13 or 0x16 or 0x17 or 0x18 or 0x19, $"PVR pixel format 0x{format:X2} is not supported.");
        Le.Need(width is > 0 and <= 16384 && height is > 0 and <= 16384 && mips < 16, $"PVR size {width}x{height} with {mips} mips is out of range.");
        int bpp = (int)Le.U32(b, 24);
        Le.Need(format is 0x18 or 0x19 || bpp is 8 or 16 or 32, $"PVR claims {bpp} bits per pixel.");
        (int, int, int)[] levels = LevelsFor(format, width, height, (int)mips + 1);
        Le.Need(HeaderSize + levels.Sum(l => (long)l.Item3) == b.Length, "PVR mip chain does not fill the file.");
        return new PvrTexture
        {
            Width = width,
            Height = height,
            Format = format,
            Flags = flags & ~0xFFu,
            Bpp = bpp,
            Masks = [Le.U32(b, 28), Le.U32(b, 32), Le.U32(b, 36), Le.U32(b, 40)],
            Levels = levels,
        };
    }

    private static (int, int, int)[] LevelsFor(int format, int width, int height, int count)
    {
        (int, int, int)[] levels = new (int, int, int)[count];
        for (int i = 0; i < count; i++)
        {
            levels[i] = (width, height, LevelSize(format, width, height));
            width = System.Math.Max(width >> 1, 1);
            height = System.Math.Max(height >> 1, 1);
        }
        return levels;
    }

    private static int LevelSize(int format, int w, int h)
    {
        return format switch
        {
            0x19 => System.Math.Max(w / 4, 2) * System.Math.Max(h / 4, 2) * 8,
            0x18 => System.Math.Max(w / 8, 2) * System.Math.Max(h / 4, 2) * 8,
            0x16 => w * h,
            0x12 => w * h * 4,
            _ => w * h * 2,
        };
    }

    /// <summary>Every mip level as upright RGBA.</summary>
    public byte[][] Decode(ReadOnlySpan<byte> file)
    {
        byte[][] planes = new byte[Levels.Length][];
        int offset = HeaderSize;
        for (int i = 0; i < Levels.Length; i++)
        {
            (int w, int h, int size) = Levels[i];
            byte[] rgba = DecodeLevel(file.Slice(offset, size), w, h);
            planes[i] = (Flags & BottomUp) != 0 ? FlipRows(rgba, w, h) : rgba;
            offset += size;
        }
        return planes;
    }

    public byte[] DecodeTop(ReadOnlySpan<byte> file)
    {
        (int w, int h, int size) = Levels[0];
        byte[] rgba = DecodeLevel(file.Slice(HeaderSize, size), w, h);
        return (Flags & BottomUp) != 0 ? FlipRows(rgba, w, h) : rgba;
    }

    /// <summary>A new file from an upright RGBA image. Unchanged pixels return the original file; a new size rebuilds the header and the mip chain.</summary>
    public byte[] Encode(ReadOnlySpan<byte> original, byte[] rgba, int width, int height)
    {
        bool same = width == Width && height == Height;
        if (same && DecodeTop(original).AsSpan().SequenceEqual(rgba))
        {
            return original.ToArray();
        }
        if (IsPvrtc)
        {
            Le.Need(BitOperations.IsPow2(width) && BitOperations.IsPow2(height),
                $"PVRTC textures need power-of-two sides; {width}x{height} is not.");
        }
        int count = same ? Levels.Length : Levels.Length > 1 ? BitOperations.Log2((uint)System.Math.Max(width, height)) + 1 : 1;
        (int W, int H, int Size)[] levels = LevelsFor(Format, width, height, count);
        using MemoryStream data = new();
        byte[] level = rgba;
        for (int i = 0; i < levels.Length; i++)
        {
            if (i > 0)
            {
                level = BoxDown(level, levels[i - 1].W, levels[i - 1].H, levels[i].W, levels[i].H);
            }
            byte[] stored = (Flags & BottomUp) != 0 ? FlipRows(level, levels[i].W, levels[i].H) : level;
            byte[] encoded = EncodeLevel(stored, levels[i].W, levels[i].H);
            Le.Need(encoded.Length == levels[i].Size, "PVR level encoded to the wrong size.");
            data.Write(encoded);
        }
        byte[] output = new byte[HeaderSize + data.Length];
        original[..HeaderSize].CopyTo(output);
        if (!same)
        {
            Span<byte> header = output;
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)height);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)(levels.Length - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)data.Length);
        }
        data.ToArray().CopyTo(output, HeaderSize);
        return output;
    }

    private byte[] DecodeLevel(ReadOnlySpan<byte> data, int w, int h)
    {
        if (IsPvrtc)
        {
            return Pvrtc.Decode(data, w, h, Format == 0x18);
        }
        Le.Need((Flags & Twiddled) == 0, "Twiddled uncompressed PVR textures are not supported.");
        int bytes = Bpp / 8;
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            uint v = bytes switch
            {
                1 => data[i],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]),
            };
            for (int c = 0; c < 4; c++)
            {
                uint m = Masks[c];
                if (m == 0)
                {
                    rgba[(i * 4) + c] = (byte)(c == 3 ? 255 : 0);
                    continue;
                }
                int shift = BitOperations.TrailingZeroCount(m);
                rgba[(i * 4) + c] = (byte)(((v & m) >> shift) * 255 / (m >> shift));
            }
        }
        return rgba;
    }

    /// <summary>Inverse of the linear decode, exact for every channel depth: round(c * max / 255) undoes c = v * 255 / max. Grey formats take the luminance.</summary>
    private byte[] EncodeLevel(byte[] rgba, int w, int h)
    {
        if (IsPvrtc)
        {
            return Pvrtc.Encode(rgba, w, h, Format == 0x18);
        }
        int bytes = Bpp / 8;
        byte[] output = new byte[w * h * bytes];
        for (int i = 0; i < w * h; i++)
        {
            int r = rgba[i * 4], g = rgba[(i * 4) + 1], b = rgba[(i * 4) + 2];
            long grey = ((r * 299) + (g * 587) + (b * 114) + 500) / 1000;
            ulong v = 0;
            for (int c = 0; c < 4; c++)
            {
                uint m = Masks[c];
                if (m == 0)
                {
                    continue;
                }
                int shift = BitOperations.TrailingZeroCount(m);
                long channel = c < 3 && Masks.Count(x => x == m) > 1 ? grey : rgba[(i * 4) + c];
                v |= (ulong)(((channel * (m >> shift)) + 127) / 255) << shift;
            }
            for (int k = 0; k < bytes; k++)
            {
                output[(i * bytes) + k] = (byte)(v >> (8 * k));
            }
        }
        return output;
    }

    private static byte[] FlipRows(byte[] rgba, int w, int h)
    {
        byte[] flipped = new byte[rgba.Length];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(rgba, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
        }
        return flipped;
    }

    private static byte[] BoxDown(byte[] p, int pw, int ph, int w, int h)
    {
        int fy = ph / h, fx = pw / w;
        int n = fy * fx;
        byte[] output = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < fy; dy++)
                    {
                        for (int dx = 0; dx < fx; dx++)
                        {
                            sum += p[((((y * fy) + dy) * pw) + (x * fx) + dx) * 4 + c];
                        }
                    }
                    output[(((y * w) + x) * 4) + c] = (byte)((sum + (n / 2)) / n);
                }
            }
        }
        return output;
    }
}

/// <summary>PVRTC 4bpp (4x4 blocks) and 2bpp (8x4 blocks), after the PowerVR SDK decoder, blocks in Morton order with the y bit lowest.</summary>
internal static class Pvrtc
{
    private static readonly int[] s_weights = [0, 3, 5, 8];
    private static readonly int[] s_punchThrough = [0, 4, 4, 8];

    /// <summary>Data index of each block, row by row: Morton order with the y bit lowest.</summary>
    private static int[] Order(int xb, int yb)
    {
        int[] order = new int[xb * yb];
        int smaller = System.Math.Min(xb, yb);
        for (int y = 0; y < yb; y++)
        {
            for (int x = 0; x < xb; x++)
            {
                int t = 0, bit = 1, destination = 1, shift = 0;
                while (bit < smaller)
                {
                    t |= ((y & bit) != 0 ? destination : 0) | ((x & bit) != 0 ? destination << 1 : 0);
                    bit <<= 1;
                    destination <<= 2;
                    shift++;
                }
                order[(y * xb) + x] = t | (((xb < yb ? y : x) >> shift) << (2 * shift));
            }
        }
        return order;
    }

    /// <summary>16-bit endpoint to r, g, b on 0-31 and alpha on 0-15. Colour A (low half) has 4-bit blue in opaque mode.</summary>
    private static void Unpack(uint raw, bool a, Span<double> rgba)
    {
        bool opaque = ((raw >> 15) & 1) != 0;
        rgba[0] = opaque ? (raw >> 10) & 31 : ((raw >> 7) & 0x1E) | ((raw >> 11) & 1);
        rgba[1] = opaque ? (raw >> 5) & 31 : ((raw >> 3) & 0x1E) | ((raw >> 7) & 1);
        rgba[2] = a
            ? opaque ? (((raw >> 1) & 15) << 1) | ((raw >> 4) & 1) : (((raw >> 1) & 7) << 2) | ((raw >> 2) & 3)
            : opaque ? raw & 31 : ((raw & 15) << 1) | ((raw >> 3) & 1);
        rgba[3] = opaque ? 15 : ((raw >> 12) & 7) << 1;
    }

    /// <summary>Bilinear endpoint image: block colours sit at block centres and wrap around the edges.</summary>
    private static double[] Up(double[] c, int xb, int yb, int bw)
    {
        int height = yb * 4, width = xb * bw;
        double[] output = new double[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            double ys = (y - 2) / 4.0;
            int y0 = (int)System.Math.Floor(ys);
            double fy = ys - y0;
            y0 = ((y0 % yb) + yb) % yb;
            int y1 = (y0 + 1) % yb;
            for (int x = 0; x < width; x++)
            {
                double xs = (x - (bw / 2.0)) / bw;
                int x0 = (int)System.Math.Floor(xs);
                double fx = xs - x0;
                x0 = ((x0 % xb) + xb) % xb;
                int x1 = (x0 + 1) % xb;
                for (int k = 0; k < 4; k++)
                {
                    double a = c[(((y0 * xb) + x0) * 4) + k], b = c[(((y0 * xb) + x1) * 4) + k];
                    double d = c[(((y1 * xb) + x0) * 4) + k], e = c[(((y1 * xb) + x1) * 4) + k];
                    output[(((y * width) + x) * 4) + k] = (((a * (1 - fx)) + (b * fx)) * (1 - fy)) + (((d * (1 - fx)) + (e * fx)) * fy);
                }
            }
        }
        return output;
    }

    private static (double[] A, double[] B) Endpoints(uint[] colours, int xb, int yb, int bw)
    {
        double[] a = new double[colours.Length * 4], b = new double[colours.Length * 4];
        for (int i = 0; i < colours.Length; i++)
        {
            Unpack(colours[i] & 0xFFFF, true, a.AsSpan(i * 4, 4));
            Unpack(colours[i] >> 16, false, b.AsSpan(i * 4, 4));
        }
        return (Up(a, xb, yb, bw), Up(b, xb, yb, bw));
    }

    public static byte[] Decode(ReadOnlySpan<byte> data, int w, int h, bool twoBpp)
    {
        int bw = twoBpp ? 8 : 4;
        int xb = System.Math.Max(w / bw, 2), yb = System.Math.Max(h / 4, 2);
        Le.Need(data.Length >= xb * yb * 8, "PVRTC level is shorter than its blocks.");
        int[] order = Order(xb, yb);
        uint[] mod = new uint[xb * yb], col = new uint[xb * yb];
        for (int i = 0; i < order.Length; i++)
        {
            mod[i] = Le.U32(data, order[i] * 8);
            col[i] = Le.U32(data, (order[i] * 8) + 4);
        }
        (double[] a, double[] b) = Endpoints(col, xb, yb, bw);
        int height = yb * 4, width = xb * bw;
        int[] weight = new int[width * height];
        bool[] transparent = new bool[width * height];
        if (!twoBpp)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int block = ((y / 4) * xb) + (x / 4);
                    int m = (int)((mod[block] >> (2 * (((y % 4) * 4) + (x % 4)))) & 3);
                    bool punch = (col[block] & 1) != 0;
                    weight[(y * width) + x] = punch ? s_punchThrough[m] : s_weights[m];
                    transparent[(y * width) + x] = punch && m == 2;
                }
            }
        }
        else
        {
            int[] mode = new int[xb * yb];
            uint[] adjusted = new uint[xb * yb];
            for (int i = 0; i < mode.Length; i++)
            {
                uint m = mod[i];
                mode[i] = (col[i] & 1) == 0 ? 0 : (m & 1) != 0 ? (m & (1u << 20)) != 0 ? 3 : 2 : 1;
                uint adj = (m & 1) != 0 ? (m & (1u << 21)) != 0 ? m | (1u << 20) : m & ~(1u << 20) : m;
                adjusted[i] = (adj & 2) != 0 ? adj | 1 : adj & ~1u;
            }
            int[] r = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int block = ((y / 4) * xb) + (x / 8);
                    int ly = y % 4, lx = x % 8;
                    int value = mode[block] == 0
                        ? (int)((mod[block] >> ((ly * 8) + lx)) & 1) * 3
                        : (int)((adjusted[block] >> (2 * ((ly * 4) + (lx / 2)))) & 3);
                    r[(y * width) + x] = s_weights[value];
                }
            }
            int At(int yy, int xx)
            {
                return r[((((yy % height) + height) % height) * width) + (((xx % width) + width) % width)];
            }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int md = mode[((y / 4) * xb) + (x / 8)];
                    bool stored = (((x % 8) ^ (y % 4)) & 1) == 0;
                    weight[(y * width) + x] = md == 0 || stored ? r[(y * width) + x]
                        : md == 1 ? (At(y - 1, x) + At(y + 1, x) + At(y, x - 1) + At(y, x + 1) + 2) / 4
                        : md == 2 ? (At(y, x - 1) + At(y, x + 1) + 1) / 2
                        : (At(y - 1, x) + At(y + 1, x) + 1) / 2;
                }
            }
        }
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int t = (y * width) + x;
                double wt = weight[t] / 8.0;
                for (int k = 0; k < 4; k++)
                {
                    double o = (a[(t * 4) + k] * (1 - wt)) + (b[(t * 4) + k] * wt);
                    double v = k < 3 ? o * 255 / 31 : transparent[t] ? 0 : o * 255 / 15;
                    rgba[(((y * w) + x) * 4) + k] = (byte)System.Math.Clamp(v, 0, 255);
                }
            }
        }
        return rgba;
    }

    /// <summary>
    /// Encoder for edited textures: each block's per-channel minimum and maximum become its endpoints, then every texel
    /// takes the modulation weight that best matches it under the decoder's own bilinear endpoint images.
    /// </summary>
    // ponytail: min/max endpoints in one pass; an optimising encoder (PVRTexTool) gives sharper results on detailed art
    public static byte[] Encode(byte[] rgba, int w, int h, bool twoBpp)
    {
        int bw = twoBpp ? 8 : 4;
        int xb = System.Math.Max(w / bw, 2), yb = System.Math.Max(h / 4, 2);
        int height = yb * 4, width = xb * bw;
        double[] image = new double[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < 4; k++)
                {
                    image[(((y * width) + x) * 4) + k] = rgba[((((y % h) * w) + (x % w)) * 4) + k];
                }
            }
        }
        uint[] colours = new uint[xb * yb];
        for (int by = 0; by < yb; by++)
        {
            for (int bx = 0; bx < xb; bx++)
            {
                Span<double> lo = [255, 255, 255, 255];
                Span<double> hi = [0, 0, 0, 0];
                for (int y = by * 4; y < (by * 4) + 4; y++)
                {
                    for (int x = bx * bw; x < (bx * bw) + bw; x++)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            double v = image[(((y * width) + x) * 4) + k];
                            lo[k] = System.Math.Min(lo[k], v);
                            hi[k] = System.Math.Max(hi[k], v);
                        }
                    }
                }
                uint a, b;
                if (lo[3] >= 255)
                {
                    a = 0x8000 | (Q(lo[0], 31) << 10) | (Q(lo[1], 31) << 5) | (Q(lo[2], 15) << 1);
                    b = 0x8000 | (Q(hi[0], 31) << 10) | (Q(hi[1], 31) << 5) | Q(hi[2], 31);
                }
                else
                {
                    a = (Q(lo[3], 7) << 12) | (Q(lo[0], 15) << 8) | (Q(lo[1], 15) << 4) | (Q(lo[2], 7) << 1);
                    b = (Q(hi[3], 7) << 12) | (Q(hi[0], 15) << 8) | (Q(hi[1], 15) << 4) | Q(hi[2], 15);
                }
                colours[(by * xb) + bx] = a | (b << 16);
            }
        }
        (double[] ai, double[] bi) = Endpoints(colours, xb, yb, bw);
        int[] candidates = twoBpp ? [0, 8] : [0, 3, 5, 8];
        int bits = twoBpp ? 1 : 2;
        uint[] modulation = new uint[xb * yb];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int t = (y * width) + x;
                int best = 0;
                double bestError = double.MaxValue;
                for (int c = 0; c < candidates.Length; c++)
                {
                    int k8 = candidates[c];
                    double error = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        double scale = k < 3 ? 255 / 31.0 : 255 / 15.0;
                        double v = (((ai[(t * 4) + k] * scale * (8 - k8)) + (bi[(t * 4) + k] * scale * k8)) / 8) - image[(t * 4) + k];
                        error += v * v;
                    }
                    if (error < bestError)
                    {
                        bestError = error;
                        best = c;
                    }
                }
                int texel = ((y % 4) * bw) + (x % bw);
                modulation[((y / 4) * xb) + (x / bw)] |= (uint)best << (bits * texel);
            }
        }
        int[] order = Order(xb, yb);
        byte[] output = new byte[xb * yb * 8];
        for (int i = 0; i < order.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(order[i] * 8), modulation[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan((order[i] * 8) + 4), colours[i]);
        }
        return output;
    }

    private static uint Q(double value, int max)
    {
        return (uint)System.Math.Clamp(System.Math.Round(value * max / 255), 0, max);
    }
}
