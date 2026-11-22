using System.Buffers.Binary;
namespace SSNCR.Formats;

// Full GCN (GameCube) texture format decoders → RGBA8 output.
// All formats use tiled layout; tiles vary by format.
// Palette decode helpers used by CI4/CI8 and TPL palette textures.

internal static class GcnTex
{
    // ── data size ─────────────────────────────────────────────────────────────

    public static int DataSize(uint fmt, int w, int h) => (int)fmt switch
    {
        0  => ((w+7)/8) * ((h+7)/8) * 32,
        1  => ((w+7)/8) * ((h+3)/4) * 32,
        2  => ((w+7)/8) * ((h+3)/4) * 32,
        3  => ((w+3)/4) * ((h+3)/4) * 32,
        4  => ((w+3)/4) * ((h+3)/4) * 32,
        5  => ((w+3)/4) * ((h+3)/4) * 32,
        6  => ((w+3)/4) * ((h+3)/4) * 64,
        8  => ((w+7)/8) * ((h+7)/8) * 32,
        9  => ((w+7)/8) * ((h+3)/4) * 32,
        14 => ((w+7)/8) * ((h+7)/8) * 32,
        _  => w * h * 4,
    };

    // ── dispatch ──────────────────────────────────────────────────────────────

    public static byte[] Decode(ReadOnlySpan<byte> raw, int w, int h, uint fmt,
        ReadOnlySpan<byte> palette = default, uint palFmt = 1)
    {
        if (w <= 0 || h <= 0) return [];
        return (int)fmt switch
        {
            0  => DecodeI4(raw, w, h),
            1  => DecodeI8(raw, w, h),
            2  => DecodeIA4(raw, w, h),
            3  => DecodeIA8(raw, w, h),
            4  => DecodeRgb565(raw, w, h),
            5  => DecodeRgb5A3(raw, w, h),
            6  => DecodeRgba8(raw, w, h),
            8  => DecodeCi4(raw, w, h, palette, palFmt),
            9  => DecodeCi8(raw, w, h, palette, palFmt),
            14 => DecodeCmpr(raw, w, h),
            _  => new byte[w * h * 4],
        };
    }

    // ── I4: 8×8 tiles, 2 pixels per byte (high nibble = left pixel) ──────────

    private static byte[] DecodeI4(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 7) / 8; ty++)
        for (int tx = 0; tx < (w + 7) / 8; tx++)
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x += 2, src++)
        {
            if (src >= raw.Length) break;
            byte b = raw[src];
            SetI(out_, tx*8+x,   ty*8+y, w, h, (byte)((b >> 4) * 17));
            SetI(out_, tx*8+x+1, ty*8+y, w, h, (byte)((b & 0xF) * 17));
        }
        return out_;
    }

    // ── I8: 8×4 tiles ────────────────────────────────────────────────────────

    private static byte[] DecodeI8(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 7) / 8; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++, src++)
        {
            if (src >= raw.Length) break;
            SetI(out_, tx*8+x, ty*4+y, w, h, raw[src]);
        }
        return out_;
    }

    // ── IA4: 8×4 tiles, high nibble=intensity, low nibble=alpha ──────────────

    private static byte[] DecodeIA4(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 7) / 8; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++, src++)
        {
            if (src >= raw.Length) break;
            byte b = raw[src];
            byte i = (byte)((b >> 4) * 17);
            byte a = (byte)((b & 0xF) * 17);
            SetIA(out_, tx*8+x, ty*4+y, w, h, i, a);
        }
        return out_;
    }

    // ── IA8: 4×4 tiles, byte0=intensity, byte1=alpha ─────────────────────────

    private static byte[] DecodeIA8(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 3) / 4; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++, src += 2)
        {
            if (src + 1 >= raw.Length) break;
            SetIA(out_, tx*4+x, ty*4+y, w, h, raw[src], raw[src+1]);
        }
        return out_;
    }

    // ── RGB565: 4×4 tiles ─────────────────────────────────────────────────────

    private static byte[] DecodeRgb565(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 3) / 4; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++, src += 2)
        {
            if (src + 1 >= raw.Length) break;
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(raw[src..]);
            SetRgb565(out_, tx*4+x, ty*4+y, w, h, v, 0xFF);
        }
        return out_;
    }

    // ── RGB5A3: 4×4 tiles; bit15=1 → RGB555, bit15=0 → RGB444+A3 ─────────────

    private static byte[] DecodeRgb5A3(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 3) / 4; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++, src += 2)
        {
            if (src + 1 >= raw.Length) break;
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(raw[src..]);
            (byte r, byte g, byte b, byte a) = (v & 0x8000) != 0
                ? ((byte)(((v >> 10) & 31) * 255 / 31),
                   (byte)(((v >> 5) & 31) * 255 / 31),
                   (byte)((v & 31) * 255 / 31),
                   (byte)255)
                : ((byte)(((v >> 8) & 15) * 17),
                   (byte)(((v >> 4) & 15) * 17),
                   (byte)((v & 15) * 17),
                   (byte)((((v >> 12) & 7) << 5) | (((v >> 12) & 7) << 2) | (((v >> 12) & 7) >> 1)));
            SetRgba(out_, tx*4+x, ty*4+y, w, h, r, g, b, a);
        }
        return out_;
    }

    // ── RGBA8: 4×4 tiles; each tile = 32 bytes AR plane + 32 bytes GB plane ──

    private static byte[] DecodeRgba8(ReadOnlySpan<byte> raw, int w, int h)
    {
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 3) / 4; tx++, src += 64)
        {
            if (src + 63 >= raw.Length) break;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int sub = y * 4 + x;
                byte a = raw[src + sub * 2];
                byte r = raw[src + sub * 2 + 1];
                byte g = raw[src + 32 + sub * 2];
                byte b = raw[src + 32 + sub * 2 + 1];
                SetRgba(out_, tx*4+x, ty*4+y, w, h, r, g, b, a);
            }
        }
        return out_;
    }

    // ── CI4: 8×8 tiles, 4bpp palette indices ──────────────────────────────────

    private static byte[] DecodeCi4(ReadOnlySpan<byte> raw, int w, int h,
        ReadOnlySpan<byte> pal, uint palFmt)
    {
        uint[] colors = DecodePalette(pal, palFmt);
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 7) / 8; ty++)
        for (int tx = 0; tx < (w + 7) / 8; tx++)
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x += 2, src++)
        {
            if (src >= raw.Length) break;
            byte b = raw[src];
            SetPal(out_, tx*8+x,   ty*8+y, w, h, colors, (b >> 4) & 0xF);
            SetPal(out_, tx*8+x+1, ty*8+y, w, h, colors, b & 0xF);
        }
        return out_;
    }

    // ── CI8: 8×4 tiles, 8bpp palette indices ──────────────────────────────────

    private static byte[] DecodeCi8(ReadOnlySpan<byte> raw, int w, int h,
        ReadOnlySpan<byte> pal, uint palFmt)
    {
        uint[] colors = DecodePalette(pal, palFmt);
        byte[] out_ = new byte[w * h * 4];
        int src = 0;
        for (int ty = 0; ty < (h + 3) / 4; ty++)
        for (int tx = 0; tx < (w + 7) / 8; tx++)
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++, src++)
        {
            if (src >= raw.Length) break;
            SetPal(out_, tx*8+x, ty*4+y, w, h, colors, raw[src]);
        }
        return out_;
    }

    // ── CMPR: 8×8 macro-tiles of 4 DXT1 sub-blocks (re-exposed from TexFile) ─

    public static byte[] DecodeCmpr(ReadOnlySpan<byte> raw, int w, int h)
        => TexFile.DecodeCmpr(raw, w, h);

    // ── palette decode ─────────────────────────────────────────────────────────
    // palFmt: 0=IA8, 1=RGB565, 2=RGB5A3

    public static uint[] DecodePalette(ReadOnlySpan<byte> pal, uint palFmt)
    {
        if (pal.IsEmpty) return [];
        int count = pal.Length / 2;
        uint[] colors = new uint[count];
        for (int i = 0; i < count; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(pal[(i * 2)..]);
            colors[i] = palFmt switch
            {
                0 => PalIA8(v),
                2 => PalRgb5A3(v),
                _ => PalRgb565(v),
            };
        }
        return colors;
    }

    private static uint PalIA8(ushort v)
    {
        byte i = (byte)(v >> 8);
        byte a = (byte)(v & 0xFF);
        return ((uint)a << 24) | ((uint)i << 16) | ((uint)i << 8) | i;
    }

    private static uint PalRgb565(ushort v)
    {
        byte r = (byte)(((v >> 11) & 31) * 255 / 31);
        byte g = (byte)(((v >> 5) & 63) * 255 / 63);
        byte b = (byte)((v & 31) * 255 / 31);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static uint PalRgb5A3(ushort v)
    {
        if ((v & 0x8000) != 0)
        {
            byte r = (byte)(((v >> 10) & 31) * 255 / 31);
            byte g = (byte)(((v >> 5) & 31) * 255 / 31);
            byte b = (byte)((v & 31) * 255 / 31);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        else
        {
            int a3 = (v >> 12) & 7;
            byte a = (byte)((a3 << 5) | (a3 << 2) | (a3 >> 1));
            byte r = (byte)(((v >> 8) & 15) * 17);
            byte g = (byte)(((v >> 4) & 15) * 17);
            byte b = (byte)((v & 15) * 17);
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    // ── pixel writers ─────────────────────────────────────────────────────────

    private static void SetI(byte[] out_, int x, int y, int w, int h, byte i)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int p = (y * w + x) * 4;
        out_[p] = i; out_[p+1] = i; out_[p+2] = i; out_[p+3] = 0xFF;
    }

    private static void SetIA(byte[] out_, int x, int y, int w, int h, byte i, byte a)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int p = (y * w + x) * 4;
        out_[p] = i; out_[p+1] = i; out_[p+2] = i; out_[p+3] = a;
    }

    private static void SetRgb565(byte[] out_, int x, int y, int w, int h, ushort v, byte a)
    {
        byte r = (byte)(((v >> 11) & 31) * 255 / 31);
        byte g = (byte)(((v >> 5) & 63) * 255 / 63);
        byte b = (byte)((v & 31) * 255 / 31);
        SetRgba(out_, x, y, w, h, r, g, b, a);
    }

    private static void SetRgba(byte[] out_, int x, int y, int w, int h,
        byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int p = (y * w + x) * 4;
        out_[p] = r; out_[p+1] = g; out_[p+2] = b; out_[p+3] = a;
    }

    private static void SetPal(byte[] out_, int x, int y, int w, int h, uint[] colors, int idx)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h || idx >= colors.Length) return;
        uint c = colors[idx];
        int p = (y * w + x) * 4;
        out_[p]   = (byte)((c >> 16) & 0xFF);
        out_[p+1] = (byte)((c >> 8) & 0xFF);
        out_[p+2] = (byte)(c & 0xFF);
        out_[p+3] = (byte)((c >> 24) & 0xFF);
    }
}
