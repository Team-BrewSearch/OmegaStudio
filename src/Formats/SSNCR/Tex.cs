using System.Buffers.Binary;
using System.Text;
using Omega.Plugins;
namespace SSNCR.Formats;

internal static class TexFile
{

    public static bool IsDictionary(ReadOnlySpan<byte> data) => Rw.IsChunk(data, 0, Rw.TexDictionary);

    public static IReadOnlyList<TexTexture> Read(ReadOnlySpan<byte> data)
    {
        if (!IsDictionary(data)) return [];
        int size = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (size < 0 || size > data.Length - 12) throw new InvalidDataException("truncated texture dictionary");
        List<TexTexture> result = [];
        foreach (RwChunk c in Rw.Children(data, 12, 12 + size))
        {
            if (c.Type != Rw.TextureNative) continue;
            RwChunk st = Rw.Require(Rw.Children(data, c), Rw.Struct);
            ReadOnlySpan<byte> h = data.Slice(st.Offset, st.Size);
            if (h.Length < 0x6C || BinaryPrimitives.ReadUInt32BigEndian(h) != Rw.PlatformGcn) continue;
            string name = Rw.CString(h.Slice(0x18, 32));
            int w = BinaryPrimitives.ReadUInt16BigEndian(h[0x5C..]);
            int ht = BinaryPrimitives.ReadUInt16BigEndian(h[0x5E..]);
            int fmt = h[0x62], palFmt = h[0x63];
            int palSize = fmt switch { 8 => 32, 9 => 512, _ => 0 };
            int dataSize = BinaryPrimitives.ReadInt32BigEndian(h[(0x68 + palSize)..]);
            if (dataSize <= 0 || 0x6C + palSize + dataSize != h.Length || w is <= 0 or > 4096 || ht is <= 0 or > 4096)
                throw new InvalidDataException($"texture '{name}' has a bad header");
            int palOff = st.Offset + 0x68;
            result.Add(new TexTexture(name, w, ht, fmt, palOff + palSize + 4, dataSize,
                palSize > 0 ? palOff : -1, palSize, palFmt));
        }
        return result;
    }

    // Decode GCN CMPR (GX_TF_CMPR = 14) → RGBA8.
    // GCN CMPR = DXT1 stored in 8×8 macro-tiles of 4 DXT1 sub-blocks,
    // with colors as big-endian RGB565.
    public static byte[] DecodeCmpr(ReadOnlySpan<byte> raw, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        int tilesX = Math.Max(1, (w + 7) / 8);
        int tilesY = Math.Max(1, (h + 7) / 8);
        int src = 0;
        for (int ty = 0; ty < tilesY && src + 32 <= raw.Length; ty++)
        for (int tx = 0; tx < tilesX; tx++, src += 32)
        {
            if (src + 32 > raw.Length) break;
            for (int sb = 0; sb < 4; sb++)
            {
                int ox = tx * 8 + (sb & 1) * 4;
                int oy = ty * 8 + (sb >> 1) * 4;
                if (ox >= w || oy >= h) continue;
                DecodeDxt1Sub(raw[(src + sb * 8)..], rgba, ox, oy, w, h);
            }
        }
        return rgba;
    }

    private static void DecodeDxt1Sub(ReadOnlySpan<byte> blk, byte[] rgba, int ox, int oy, int w, int h)
    {
        // Colors big-endian in GCN CMPR
        ushort c0 = BinaryPrimitives.ReadUInt16BigEndian(blk);
        ushort c1 = BinaryPrimitives.ReadUInt16BigEndian(blk[2..]);
        Span<(byte r, byte g, byte b, byte a)> col = stackalloc (byte, byte, byte, byte)[4];
        col[0] = Rgb565(c0, 0xFF);
        col[1] = Rgb565(c1, 0xFF);
        if (c0 > c1)
        {
            col[2] = Interp(col[0], col[1], 5, 3);
            col[3] = Interp(col[0], col[1], 3, 5);
        }
        else
        {
            col[2] = Interp(col[0], col[1], 1, 1);
            col[3] = (col[2].r, col[2].g, col[2].b, 0);
        }
        for (int row = 0; row < 4; row++)
        {
            byte bits = blk[4 + row];
            for (int col2 = 0; col2 < 4; col2++)
            {
                int px = ox + col2, py = oy + row;
                if (px >= w || py >= h) continue;
                int idx = (bits >> (6 - col2 * 2)) & 3;
                var (r, g, b, a) = col[idx];
                int p = (py * w + px) * 4;
                rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = a;
            }
        }
    }

    private static (byte r, byte g, byte b, byte a) Rgb565(ushort v, byte a)
    {
        int r = v >> 11 & 31, g = v >> 5 & 63, b = v & 31;
        return ((byte)(r << 3 | r >> 2), (byte)(g << 2 | g >> 4), (byte)(b << 3 | b >> 2), a);
    }

    private static (byte r, byte g, byte b, byte a) Interp(
        (byte r, byte g, byte b, byte a) a, (byte r, byte g, byte b, byte a) b, int fa, int fb)
    {
        int s = fa + fb;
        return ((byte)((a.r * fa + b.r * fb) / s),
                (byte)((a.g * fa + b.g * fb) / s),
                (byte)((a.b * fa + b.b * fb) / s),
                0xFF);
    }
}

internal sealed record TexTexture(string Name, int Width, int Height, int GcnFmt,
    int DataOffset, int DataSize, int PaletteOffset = -1, int PaletteSize = 0, int PaletteFormat = 1);
