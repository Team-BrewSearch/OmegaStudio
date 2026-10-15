namespace DWSSK.Formats.Textures;

internal static class Bcn
{
    public static int Size(int w, int h, int n)
    {
        return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (n == 1 ? 8 : 16);
    }

    private static (int R, int G, int B) Decode565(uint x)
    {
        int r = (int)((x & 0xF800) >> 8);
        r |= r >> 5;
        int g = (int)((x & 0x7E0) >> 3);
        g |= g >> 6;
        int b = (int)((x & 0x1F) << 3);
        b |= b >> 5;
        return (r, g, b);
    }

    public static byte[] Decode(ReadOnlySpan<byte> data, int w, int h, int n)
    {
        int bw = Math.Max(1, (w + 3) / 4);
        int bh = Math.Max(1, (h + 3) / 4);
        int blockSize = n == 1 ? 8 : 16;
        if (data.Length < bw * bh * blockSize)
        {
            throw new InvalidDataException($"BC{n} data holds {data.Length} bytes, a {w}x{h} surface needs {bw * bh * blockSize}");
        }
        int pw = bw * 4;
        byte[] full = new byte[pw * bh * 4 * 4];
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                ReadOnlySpan<byte> src = data.Slice(((by * bw) + bx) * blockSize, blockSize);
                if (n == 1)
                {
                    Color(src, px, false);
                }
                else
                {
                    Color(src[8..], px, true);
                    if (n == 2)
                    {
                        Bc2Alpha(src, px);
                    }
                    else
                    {
                        Bc3Alpha(src, px);
                    }
                }
                for (int y = 0; y < 4; y++)
                {
                    px.Slice(y * 16, 16).CopyTo(full.AsSpan(((((by * 4) + y) * pw) + (bx * 4)) * 4));
                }
            }
        }
        return Pixels.Crop(Rows(full, pw, h), pw, w, h);
    }

    private static byte[] Rows(byte[] full, int pw, int h)
    {
        return full.Length == pw * h * 4 ? full : full.AsSpan(0, pw * h * 4).ToArray();
    }

    private static void Color(ReadOnlySpan<byte> src, Span<byte> px, bool separateAlpha)
    {
        uint c0 = (uint)(src[0] | (src[1] << 8));
        uint c1 = (uint)(src[2] | (src[3] << 8));
        uint lut = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
        (int r0, int g0, int b0) = Decode565(c0);
        (int r1, int g1, int b1) = Decode565(c1);
        Span<int> p = stackalloc int[16];
        p[0] = r0;
        p[1] = g0;
        p[2] = b0;
        p[3] = 255;
        p[4] = r1;
        p[5] = g1;
        p[6] = b1;
        p[7] = 255;
        if (c0 > c1 || separateAlpha)
        {
            p[8] = ((2 * r0) + r1) / 3;
            p[9] = ((2 * g0) + g1) / 3;
            p[10] = ((2 * b0) + b1) / 3;
            p[11] = 255;
            p[12] = (r0 + (2 * r1)) / 3;
            p[13] = (g0 + (2 * g1)) / 3;
            p[14] = (b0 + (2 * b1)) / 3;
            p[15] = 255;
        }
        else
        {
            p[8] = (r0 + r1) / 2;
            p[9] = (g0 + g1) / 2;
            p[10] = (b0 + b1) / 2;
            p[11] = 255;
            p[12] = 0;
            p[13] = 0;
            p[14] = 0;
            p[15] = 0;
        }
        for (int k = 0; k < 16; k++)
        {
            int cw = (int)(3 & (lut >> (2 * k)));
            px[k * 4] = (byte)p[cw * 4];
            px[(k * 4) + 1] = (byte)p[(cw * 4) + 1];
            px[(k * 4) + 2] = (byte)p[(cw * 4) + 2];
            px[(k * 4) + 3] = (byte)p[(cw * 4) + 3];
        }
    }

    private static void Bc2Alpha(ReadOnlySpan<byte> src, Span<byte> px)
    {
        for (int k = 0; k < 16; k++)
        {
            int bit = k * 4;
            int av = 0xF & (src[bit >> 3] >> (bit & 7));
            px[(k * 4) + 3] = (byte)((av << 4) | av);
        }
    }

    private static void Bc3Alpha(ReadOnlySpan<byte> src, Span<byte> px)
    {
        int a0 = src[0];
        int a1 = src[1];
        Span<int> a = stackalloc int[8];
        a[0] = a0;
        a[1] = a1;
        if (a0 > a1)
        {
            a[2] = ((6 * a0) + a1) / 7;
            a[3] = ((5 * a0) + (2 * a1)) / 7;
            a[4] = ((4 * a0) + (3 * a1)) / 7;
            a[5] = ((3 * a0) + (4 * a1)) / 7;
            a[6] = ((2 * a0) + (5 * a1)) / 7;
            a[7] = (a0 + (6 * a1)) / 7;
        }
        else
        {
            a[2] = ((4 * a0) + a1) / 5;
            a[3] = ((3 * a0) + (2 * a1)) / 5;
            a[4] = ((2 * a0) + (3 * a1)) / 5;
            a[5] = (a0 + (4 * a1)) / 5;
            a[6] = 0;
            a[7] = 255;
        }
        int lut1 = src[2] | (src[3] << 8) | (src[4] << 16);
        int lut2 = src[5] | (src[6] << 8) | (src[7] << 16);
        for (int k = 0; k < 8; k++)
        {
            px[(k * 4) + 3] = (byte)a[7 & (lut1 >> (3 * k))];
            px[((8 + k) * 4) + 3] = (byte)a[7 & (lut2 >> (3 * k))];
        }
    }

    public static byte[] Encode(byte[] img, int w, int h, int n)
    {
        int pw = (w + 3) / 4 * 4;
        int ph = (h + 3) / 4 * 4;
        byte[] padded = Pixels.PadEdge(img, w, h, pw, ph);
        int bw = pw / 4;
        int bh = ph / 4;
        int blockSize = n == 1 ? 8 : 16;
        byte[] output = new byte[Math.Max(1, bw) * Math.Max(1, bh) * blockSize];
        Span<byte> block = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    padded.AsSpan(((((by * 4) + y) * pw) + (bx * 4)) * 4, 16).CopyTo(block[(y * 16)..]);
                }
                Span<byte> dst = output.AsSpan(((by * bw) + bx) * blockSize, blockSize);
                if (n == 1)
                {
                    EncodeColor(block, dst, true);
                }
                else if (n == 2)
                {
                    EncodeBc2Alpha(block, dst);
                    EncodeColor(block, dst[8..], false);
                }
                else
                {
                    EncodeBc3Alpha(block, dst);
                    EncodeColor(block, dst[8..], false);
                }
            }
        }
        return output;
    }

    private static uint Encode565(ReadOnlySpan<byte> c)
    {
        return ((uint)(c[0] >> 3) << 11) | ((uint)(c[1] >> 2) << 5) | (uint)(c[2] >> 3);
    }

    private static void EncodeColor(ReadOnlySpan<byte> block, Span<byte> dst, bool separateAlpha)
    {
        uint min = 0;
        uint max = 0;
        bool first = true;
        bool transparency = false;
        Span<bool> opaque = stackalloc bool[16];
        for (int k = 0; k < 16; k++)
        {
            ReadOnlySpan<byte> c = block.Slice(k * 4, 4);
            opaque[k] = true;
            if (separateAlpha)
            {
                if (c[3] == 0)
                {
                    opaque[k] = false;
                    transparency = true;
                    continue;
                }
            }
            uint color = Encode565(c);
            if (first || color < min)
            {
                min = color;
            }
            if (first || color > max)
            {
                max = color;
            }
            first = false;
        }
        int at = 0;
        if (transparency)
        {
            dst[at++] = (byte)min;
            dst[at++] = (byte)(min >> 8);
        }
        dst[at++] = (byte)max;
        dst[at++] = (byte)(max >> 8);
        if (!transparency)
        {
            dst[at++] = (byte)min;
            dst[at++] = (byte)(min >> 8);
        }
        (int r0, int g0, int b0) = Decode565(min);
        (int r1, int g1, int b1) = Decode565(max);
        Span<int> lo = [r0, g0, b0];
        Span<int> hi = [r1, g1, b1];
        for (int row = 0; row < 4; row++)
        {
            int l = 0;
            for (int col = 3; col > -1; col--)
            {
                int k = (row * 4) + col;
                if (transparency && !opaque[k])
                {
                    l |= 3 << (col * 2);
                    continue;
                }
                float distance = 0;
                int total = 0;
                for (int c = 0; c < 3; c++)
                {
                    float denom = Math.Abs(hi[c] - lo[c]);
                    if (denom != 0)
                    {
                        distance += Math.Abs(block[(k * 4) + c] - lo[c]) / denom;
                        total++;
                    }
                }
                if (total == 0)
                {
                    continue;
                }
                if (transparency)
                {
                    distance *= 4 / total;
                    if (distance >= 1)
                    {
                        l |= (distance < 3 ? 2 : 1) << (col * 2);
                    }
                }
                else
                {
                    distance *= 6 / total;
                    if (distance < 1)
                    {
                        l |= 1 << (col * 2);
                    }
                    else if (distance < 3)
                    {
                        l |= 3 << (col * 2);
                    }
                    else if (distance < 5)
                    {
                        l |= 2 << (col * 2);
                    }
                }
            }
            dst[at++] = (byte)l;
        }
    }

    private static void EncodeBc2Alpha(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        for (int row = 0; row < 4; row++)
        {
            ushort l = 0;
            for (int col = 3; col > -1; col--)
            {
                int alpha = block[(((row * 4) + col) * 4) + 3];
                l = (ushort)(l | (alpha << (col * 4)));
            }
            dst[row * 2] = (byte)l;
            dst[(row * 2) + 1] = (byte)(l >> 8);
        }
    }

    private static void EncodeBc3Alpha(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        int min = 0;
        int max = 0;
        bool first = true;
        for (int k = 0; k < 16; k++)
        {
            int a = block[(k * 4) + 3];
            if (first || a < min)
            {
                min = a;
            }
            if (first || a > max)
            {
                max = a;
            }
            first = false;
        }
        dst[0] = (byte)min;
        dst[1] = (byte)max;
        float denom = Math.Abs(max - min);
        for (int half = 0; half < 2; half++)
        {
            int l = 0;
            for (int j = 7; j > -1; j--)
            {
                int a = block[(((half * 8) + j) * 4) + 3];
                if (a == 0)
                {
                    l |= 6 << (j * 3);
                    continue;
                }
                if (a == 255)
                {
                    l |= 7 << (j * 3);
                    continue;
                }
                float distance = denom == 0 ? 0 : Math.Abs(a - min) / denom * 10;
                l |= (distance < 3 ? 2 : distance < 5 ? 3 : distance < 7 ? 4 : 5) << (j * 3);
            }
            dst[2 + (half * 3)] = (byte)l;
            dst[3 + (half * 3)] = (byte)(l >> 8);
            dst[4 + (half * 3)] = (byte)(l >> 16);
        }
    }
}
