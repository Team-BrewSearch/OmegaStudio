namespace DWSSK.Formats.Textures;
internal static class Pixels
{
    public static byte Expand(uint v, int bits)
    {
        uint max = (1u << bits) - 1;
        return (byte)(((v * 255) + (max / 2)) / max);
    }
    public static uint Quant(uint c, int bits)
    {
        uint m = (1u << bits) - 1;
        return ((c * m) + 127) / 255;
    }
    public static uint Lum(byte r, byte g, byte b)
    {
        return ((299u * r) + (587u * g) + (114u * b) + 500) / 1000;
    }
    public static void Put(byte[] rgba, int i, uint r, uint g, uint b, uint a)
    {
        rgba[i * 4] = (byte)r;
        rgba[(i * 4) + 1] = (byte)g;
        rgba[(i * 4) + 2] = (byte)b;
        rgba[(i * 4) + 3] = (byte)a;
    }
    public static void Rgb5a3(uint v, byte[] o, int i)
    {
        if ((v & 0x8000) != 0)
        {
            Put(o, i, Expand((v >> 10) & 31, 5), Expand((v >> 5) & 31, 5), Expand(v & 31, 5), 255);
        }
        else
        {
            Put(o, i, Expand((v >> 8) & 15, 4), Expand((v >> 4) & 15, 4), Expand(v & 15, 4), Expand((v >> 12) & 7, 3));
        }
    }
    public static void Rgb565(uint v, byte[] o, int i)
    {
        Put(o, i, Expand(v >> 11, 5), Expand((v >> 5) & 63, 6), Expand(v & 31, 5), 255);
    }
    public static void Argb4444(uint v, byte[] o, int i)
    {
        Put(o, i, Expand((v >> 8) & 15, 4), Expand((v >> 4) & 15, 4), Expand(v & 15, 4), Expand(v >> 12, 4));
    }
    public static void Rgba4444(uint v, byte[] o, int i)
    {
        Put(o, i, Expand(v >> 12, 4), Expand((v >> 8) & 15, 4), Expand((v >> 4) & 15, 4), Expand(v & 15, 4));
    }
    public static void Argb1555(uint v, byte[] o, int i)
    {
        Put(o, i, Expand((v >> 10) & 31, 5), Expand((v >> 5) & 31, 5), Expand(v & 31, 5), (v >> 15) != 0 ? 255u : 0u);
    }
    public static void Rgba5551(uint v, byte[] o, int i)
    {
        Put(o, i, Expand(v >> 11, 5), Expand((v >> 6) & 31, 5), Expand((v >> 1) & 31, 5), (v & 1) != 0 ? 255u : 0u);
    }
    public static void La8(uint v, byte[] o, int i)
    {
        uint l = v & 0xFF;
        Put(o, i, l, l, l, v >> 8);
    }
    public static uint EncArgb4444(ReadOnlySpan<byte> p)
    {
        return (Quant(p[3], 4) << 12) | (Quant(p[0], 4) << 8) | (Quant(p[1], 4) << 4) | Quant(p[2], 4);
    }
    public static uint EncRgba4444(ReadOnlySpan<byte> p)
    {
        return (Quant(p[0], 4) << 12) | (Quant(p[1], 4) << 8) | (Quant(p[2], 4) << 4) | Quant(p[3], 4);
    }
    public static uint EncArgb1555(ReadOnlySpan<byte> p)
    {
        return ((p[3] >= 128 ? 1u : 0u) << 15) | (Quant(p[0], 5) << 10) | (Quant(p[1], 5) << 5) | Quant(p[2], 5);
    }
    public static uint EncRgba5551(ReadOnlySpan<byte> p)
    {
        return (Quant(p[0], 5) << 11) | (Quant(p[1], 5) << 6) | (Quant(p[2], 5) << 1) | (p[3] >= 128 ? 1u : 0u);
    }
    public static uint EncRgb565(ReadOnlySpan<byte> p)
    {
        return (Quant(p[0], 5) << 11) | (Quant(p[1], 6) << 5) | Quant(p[2], 5);
    }
    public static uint EncLa8(ReadOnlySpan<byte> p)
    {
        return ((uint)p[3] << 8) | Lum(p[0], p[1], p[2]);
    }
    public static (int[] X, int[] Y) MortonXY(int w, int h)
    {
        int n = w * h;
        int[] x = new int[n];
        int[] y = new int[n];
        for (int i = 0; i < n; i++)
        {
            int bit = 0;
            int xb = 0;
            int yb = 0;
            int xv = 0;
            int yv = 0;
            while ((1 << xb) < w || (1 << yb) < h)
            {
                if ((1 << xb) < w)
                {
                    xv |= ((i >> bit) & 1) << xb;
                    xb++;
                    bit++;
                }
                if ((1 << yb) < h)
                {
                    yv |= ((i >> bit) & 1) << yb;
                    yb++;
                    bit++;
                }
            }
            x[i] = xv;
            y[i] = yv;
        }
        return (x, y);
    }
    public static byte[] Unswizzle(byte[] px, int unit, int w, int h)
    {
        (int[] xs, int[] ys) = MortonXY(w, h);
        byte[] output = new byte[w * h * unit];
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] >= w || ys[i] >= h)
            {
                throw new InvalidDataException($"a {w}x{h} surface cannot be Morton swizzled");
            }
            Buffer.BlockCopy(px, i * unit, output, ((ys[i] * w) + xs[i]) * unit, unit);
        }
        return output;
    }
    public static byte[] Swizzle(byte[] img, int unit, int w, int h)
    {
        (int[] xs, int[] ys) = MortonXY(w, h);
        byte[] output = new byte[w * h * unit];
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] >= w || ys[i] >= h)
            {
                throw new InvalidDataException($"a {w}x{h} surface cannot be Morton swizzled");
            }
            Buffer.BlockCopy(img, ((ys[i] * w) + xs[i]) * unit, output, i * unit, unit);
        }
        return output;
    }
    public static byte[] Untile(byte[] units, int unit, int w, int h, int tw, int th)
    {
        int pw = (w + tw - 1) / tw * tw;
        int ph = (h + th - 1) / th * th;
        if (units.Length < pw * ph * unit)
        {
            throw new InvalidDataException($"tiled data holds {units.Length / unit} texels, a {w}x{h} surface needs {pw * ph}");
        }
        byte[] output = new byte[w * h * unit];
        int tilesX = pw / tw;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int tile = ((y / th) * tilesX) + (x / tw);
                int k = (tile * tw * th) + ((y % th) * tw) + (x % tw);
                Buffer.BlockCopy(units, k * unit, output, ((y * w) + x) * unit, unit);
            }
        }
        return output;
    }
    public static byte[] Tile(byte[] px, int unit, int w, int h, int tw, int th)
    {
        int pw = (w + tw - 1) / tw * tw;
        int ph = (h + th - 1) / th * th;
        byte[] output = new byte[pw * ph * unit];
        int tilesX = pw / tw;
        for (int y = 0; y < ph; y++)
        {
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(x, w - 1);
                int sy = Math.Min(y, h - 1);
                int tile = ((y / th) * tilesX) + (x / tw);
                int k = (tile * tw * th) + ((y % th) * tw) + (x % tw);
                Buffer.BlockCopy(px, ((sy * w) + sx) * unit, output, k * unit, unit);
            }
        }
        return output;
    }
    public static byte[] PadEdge(byte[] img, int w, int h, int pw, int ph)
    {
        if (pw == w && ph == h)
        {
            return img;
        }
        byte[] output = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(x, w - 1);
                Buffer.BlockCopy(img, ((sy * w) + sx) * 4, output, ((y * pw) + x) * 4, 4);
            }
        }
        return output;
    }
    public static byte[] Crop(byte[] img, int pw, int w, int h)
    {
        if (pw == w)
        {
            return img.Length == w * h * 4 ? img : img.AsSpan(0, w * h * 4).ToArray();
        }
        byte[] output = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(img, y * pw * 4, output, y * w * 4, w * 4);
        }
        return output;
    }
    public static List<(byte[] Image, int Width, int Height)> MipChain(byte[] img, int w, int h, int n)
    {
        List<(byte[], int, int)> output = [(img, w, h)];
        for (int level = 1; level < n; level++)
        {
            (byte[] a, int aw, int ah) = output[^1];
            uint[] cur = new uint[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                cur[i] = a[i];
            }
            int cw = aw;
            int ch = ah;
            if (ah > 1)
            {
                int nh = ah / 2;
                uint[] next = new uint[nh * cw * 4];
                for (int y = 0; y < nh; y++)
                {
                    for (int k = 0; k < cw * 4; k++)
                    {
                        next[(y * cw * 4) + k] = (cur[(2 * y * cw * 4) + k] + cur[(((2 * y) + 1) * cw * 4) + k] + 1) / 2;
                    }
                }
                cur = next;
                ch = nh;
            }
            if (aw > 1)
            {
                int nw = aw / 2;
                uint[] next = new uint[ch * nw * 4];
                for (int y = 0; y < ch; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            next[(((y * nw) + x) * 4) + c] =
                                (cur[(((y * cw) + (2 * x)) * 4) + c] + cur[(((y * cw) + (2 * x) + 1) * 4) + c] + 1) / 2;
                        }
                    }
                }
                cur = next;
                cw = nw;
            }
            byte[] b = new byte[cur.Length];
            for (int i = 0; i < cur.Length; i++)
            {
                b[i] = (byte)cur[i];
            }
            output.Add((b, cw, ch));
        }
        return output;
    }
}
