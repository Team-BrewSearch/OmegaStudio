using DWSSK.Formats.Core;

namespace DWSSK.Formats.Textures;

internal sealed record TplImage(int Header, int Height, int Width, int Format, int DataOffset);

internal static class Tpl
{
    public const uint Magic = 0x0020AF30;

    private static (int Tw, int Th, int Bits) Tiles(int fmt)
    {
        return fmt switch
        {
            0 => (8, 8, 4),
            1 or 2 => (8, 4, 8),
            3 or 4 or 5 => (4, 4, 16),
            6 => (4, 4, 32),
            _ => throw new NotSupportedException($"GX format {fmt}"),
        };
    }

    public static readonly string[] Names = ["I4", "I8", "IA4", "IA8", "RGB565", "RGB5A3", "RGBA8"];

    public static bool IsTpl(ReadOnlySpan<byte> b)
    {
        return b.Length >= 12 && Bin.U32(b, 0, true) == Magic;
    }

    public static List<TplImage> Images(ReadOnlySpan<byte> b)
    {
        if (Bin.U32(b, 0, true) != Magic)
        {
            throw new InvalidDataException("not a TPL");
        }
        int n = (int)Bin.U32(b, 4, true);
        int tab = (int)Bin.U32(b, 8, true);
        List<TplImage> output = [];
        for (int i = 0; i < n; i++)
        {
            int ho = (int)Bin.U32(b, tab + (8 * i), true);
            int po = (int)Bin.U32(b, tab + (8 * i) + 4, true);
            int h = Bin.U16(b, ho, true);
            int w = Bin.U16(b, ho + 2, true);
            int fmt = (int)Bin.U32(b, ho + 4, true);
            int dataOffset = (int)Bin.U32(b, ho + 8, true);
            if (po != 0 || fmt is < 0 or > 6)
            {
                throw new NotSupportedException($"GX format {fmt}" + (po != 0 ? " with a palette" : string.Empty));
            }
            output.Add(new TplImage(ho, h, w, fmt, dataOffset));
        }
        return output;
    }

    public static int Size(int fmt, int w, int h)
    {
        (int tw, int th, int bits) = Tiles(fmt);
        return Bin.CeilDiv(w, tw) * tw * (Bin.CeilDiv(h, th) * th) * bits / 8;
    }

    public static byte[] Decode(int fmt, ReadOnlySpan<byte> data, int w, int h)
    {
        (int tw, int th, int bits) = Tiles(fmt);
        int pw = Bin.CeilDiv(w, tw) * tw;
        int ph = Bin.CeilDiv(h, th) * th;
        int n = pw * ph;
        Bin.Need(data, 0, n * bits / 8);
        byte[] units = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            if (fmt == 6)
            {
                int t = i / 16;
                int k = i % 16;
                int ar = (t * 64) + (k * 2);
                int gb = (t * 64) + 32 + (k * 2);
                Pixels.Put(units, i, data[ar + 1], data[gb], data[gb + 1], data[ar]);
                continue;
            }
            uint v = bits switch
            {
                4 => (uint)(i % 2 == 0 ? data[i / 2] >> 4 : data[i / 2] & 15),
                8 => data[i],
                _ => Bin.U16(data, i * 2, true),
            };
            switch (fmt)
            {
                case 0:
                {
                    byte x = Pixels.Expand(v, 4);
                    Pixels.Put(units, i, x, x, x, x);
                    break;
                }
                case 1:
                    Pixels.Put(units, i, v, v, v, v);
                    break;
                case 2:
                {
                    byte a = Pixels.Expand(v >> 4, 4);
                    byte x = Pixels.Expand(v & 15, 4);
                    Pixels.Put(units, i, x, x, x, a);
                    break;
                }
                case 3:
                    Pixels.Put(units, i, v & 255, v & 255, v & 255, v >> 8);
                    break;
                case 4:
                    Pixels.Rgb565(v, units, i);
                    break;
                default:
                    Pixels.Rgb5a3(v, units, i);
                    break;
            }
        }
        return Pixels.Untile(units, 4, w, h, tw, th);
    }

    public static byte[] Encode(int fmt, byte[] rgba, int w, int h)
    {
        (int tw, int th, int bits) = Tiles(fmt);
        int ph = Bin.CeilDiv(h, th) * th;
        int pw = Bin.CeilDiv(w, tw) * tw;
        byte[] p = new byte[pw * ph * 4];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(rgba, y * w * 4, p, y * pw * 4, w * 4);
        }
        int n = pw * ph;
        uint[] v = new uint[n];
        int tilesX = pw / tw;
        for (int i = 0; i < n; i++)
        {
            int tile = i / (tw * th);
            int k = i % (tw * th);
            int y = ((tile / tilesX) * th) + (k / tw);
            int x = ((tile % tilesX) * tw) + (k % tw);
            int o = ((y * pw) + x) * 4;
            uint r = p[o];
            uint g = p[o + 1];
            uint b = p[o + 2];
            uint a = p[o + 3];
            uint inten = ((r * 77) + (g * 150) + (b * 29) + 128) >> 8;
            if (r == g && g == b)
            {
                inten = r;
            }
            v[i] = fmt switch
            {
                0 => Pixels.Quant(inten, 4),
                1 => inten,
                2 => (Pixels.Quant(a, 4) << 4) | Pixels.Quant(inten, 4),
                3 => (a << 8) | inten,
                4 => (Pixels.Quant(r, 5) << 11) | (Pixels.Quant(g, 6) << 5) | Pixels.Quant(b, 5),
                5 => a == 255
                    ? 0x8000 | (Pixels.Quant(r, 5) << 10) | (Pixels.Quant(g, 5) << 5) | Pixels.Quant(b, 5)
                    : (Pixels.Quant(a, 3) << 12) | (Pixels.Quant(r, 4) << 8) | (Pixels.Quant(g, 4) << 4) | Pixels.Quant(b, 4),
                _ => (r << 24) | (g << 16) | (b << 8) | a,
            };
        }
        ByteWriter output = new(n * bits / 8);
        if (fmt == 6)
        {
            for (int t = 0; t < n / 16; t++)
            {
                for (int k = 0; k < 16; k++)
                {
                    uint c = v[(t * 16) + k];
                    output.Add((byte)c);
                    output.Add((byte)(c >> 24));
                }
                for (int k = 0; k < 16; k++)
                {
                    uint c = v[(t * 16) + k];
                    output.Add((byte)(c >> 16));
                    output.Add((byte)(c >> 8));
                }
            }
            return output.ToArray();
        }
        for (int i = 0; i < n; i++)
        {
            if (bits == 4)
            {
                if (i % 2 == 0)
                {
                    output.Add((byte)((v[i] << 4) | v[i + 1]));
                }
            }
            else if (bits == 8)
            {
                output.Add((byte)v[i]);
            }
            else
            {
                output.U16(v[i], true);
            }
        }
        return output.ToArray();
    }
}
