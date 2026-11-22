using System.Buffers.Binary;
namespace SSNCR.Formats;

internal sealed class Ps2Gs
{
    public const int Ct32 = 0x00, Ct24 = 0x01, Ct16 = 0x02, T8 = 0x13, T4 = 0x14;
    private const int Bytes = 4 * 1024 * 1024;
    private static readonly int[,] Block32 =
    {
        { 0, 1, 4, 5, 16, 17, 20, 21 },
        { 2, 3, 6, 7, 18, 19, 22, 23 },
        { 8, 9, 12, 13, 24, 25, 28, 29 },
        { 10, 11, 14, 15, 26, 27, 30, 31 },
    };
    private static readonly int[,] Block4 =
    {
        { 0, 2, 8, 10 }, { 1, 3, 9, 11 }, { 4, 6, 12, 14 }, { 5, 7, 13, 15 },
        { 16, 18, 24, 26 }, { 17, 19, 25, 27 }, { 20, 22, 28, 30 }, { 21, 23, 29, 31 },
    };
    private readonly byte[] _mem = new byte[Bytes];

    private static int Wrap(long a) => (int)(a & (Bytes - 1));

    private static int Addr32(int x, int y, int bp, int bw)
    {
        int page = (y >> 5) * Math.Max(bw, 1) + (x >> 6);
        int block = bp + page * 32 + Block32[(y >> 3) & 3, (x >> 3) & 7];
        int cx = x & 7, cy = y & 7;
        int word = (cy >> 1) * 16 + (cy & 1) * 2 + (cx & 1) + (cx >> 1) * 4;
        return Wrap(block * 256L + word * 4);
    }

    private static int Addr16(int x, int y, int bp, int bw)
    {
        int page = (y >> 6) * Math.Max(bw, 1) + (x >> 6);
        int block = bp + page * 32 + Block4[(y >> 3) & 7, (x >> 4) & 3];
        int cx = x & 15, cy = y & 7;
        int half = (cy >> 1) * 32 + (cy & 1) * 4 + (cx & 1) * 2 + ((cx >> 1) & 3) * 8 + (cx >> 3);
        return Wrap(block * 256L + half * 2);
    }

    private static int Addr8(int x, int y, int bp, int bw)
    {
        int page = (y >> 6) * Math.Max(bw >> 1, 1) + (x >> 7);
        int block = bp + page * 32 + Block32[(y >> 4) & 3, (x >> 4) & 7];
        int cx = x & 15, cy = y & 15, c = cy >> 2, r = cy & 3, k = cx & 7;
        int kk = (k >> 1) ^ ((((r >> 1) ^ (c & 1)) != 0) ? 2 : 0);
        int b = c * 64 + (r & 1) * 8 + (r >> 1) + (k & 1) * 4 + kk * 16 + (cx >> 3) * 2;
        return Wrap(block * 256L + b);
    }

    private static int Nibble4(int x, int y, int bp, int bw)
    {
        int page = (y >> 7) * Math.Max(bw >> 1, 1) + (x >> 7);
        int block = bp + page * 32 + Block4[(y >> 4) & 7, (x >> 5) & 3];
        int cx = x & 31, cy = y & 15, c = cy >> 2, r = cy & 3, k = cx & 7;
        int kk = (k >> 1) ^ ((((r >> 1) ^ (c & 1)) != 0) ? 2 : 0);
        int n = c * 128 + (r & 1) * 16 + (r >> 1) + (k & 1) * 8 + kk * 32 + (cx >> 3) * 2;
        return (int)((block * 512L + n) & (2L * Bytes - 1));
    }

    public void Upload(ReadOnlySpan<byte> src, int dbp, int dbw, int dpsm, int dx, int dy, int w, int h)
    {
        int bpp = dpsm switch { Ct32 => 32, Ct24 => 24, Ct16 => 16, T8 => 8, T4 => 4, _ => throw new NotSupportedException($"GS transfer PSM 0x{dpsm:X}") };
        if ((long)w * h * bpp / 8 > src.Length) throw new InvalidDataException("GS image transfer is short");
        int i = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++, i++)
            {
                int px = dx + x, py = dy + y;
                switch (dpsm)
                {
                    case Ct32:
                        src.Slice(i * 4, 4).CopyTo(_mem.AsSpan(Addr32(px, py, dbp, dbw)));
                        break;
                    case Ct16:
                        src.Slice(i * 2, 2).CopyTo(_mem.AsSpan(Addr16(px, py, dbp, dbw)));
                        break;
                    case Ct24:
                        src.Slice(i * 3, 3).CopyTo(_mem.AsSpan(Addr32(px, py, dbp, dbw)));
                        break;
                    case T8:
                        _mem[Addr8(px, py, dbp, dbw)] = src[i];
                        break;
                    case T4:
                        SetNibble(Nibble4(px, py, dbp, dbw), (src[i >> 1] >> ((i & 1) * 4)) & 15);
                        break;
                    default:
                        throw new NotSupportedException($"GS transfer PSM 0x{dpsm:X}");
                }
            }
        }
    }

    private void SetNibble(int n, int v)
    {
        int b = n >> 1;
        _mem[b] = (n & 1) == 0 ? (byte)((_mem[b] & 0xF0) | v) : (byte)((_mem[b] & 0x0F) | (v << 4));
    }

    public uint Read32(int x, int y, int bp, int bw) => BinaryPrimitives.ReadUInt32LittleEndian(_mem.AsSpan(Addr32(x, y, bp, bw)));

    public int ReadIndex(int psm, int x, int y, int bp, int bw) => psm switch
    {
        T8 => _mem[Addr8(x, y, bp, bw)],
        T4 => Nibble(Nibble4(x, y, bp, bw)),
        _ => throw new NotSupportedException($"indexed PSM 0x{psm:X}"),
    };

    private int Nibble(int n) => (_mem[n >> 1] >> ((n & 1) * 4)) & 15;

    public static byte Alpha(uint a) => (byte)Math.Min(255, a * 255 / 128);

    public static uint[] Clut(int psm, ReadOnlySpan<byte> data, int rectWidth)
    {
        int n = psm == T8 ? 256 : 16, cw = psm == T8 ? 16 : 8;
        if (rectWidth < cw) throw new InvalidDataException($"CLUT upload is {rectWidth} wide");
        var clut = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int j = psm == T8 ? (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1) : i;
            int at = ((j / cw) * rectWidth + j % cw) * 4;
            if (at + 4 > data.Length) throw new InvalidDataException("CLUT upload is short");
            clut[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        }
        return clut;
    }

    public byte[] ReadTexture(ulong tex0, int width, int height, uint[]? clut)
    {
        int tbp = (int)(tex0 & 0x3FFF), tbw = (int)((tex0 >> 14) & 0x3F), psm = (int)((tex0 >> 20) & 0x3F);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint c = psm switch
                {
                    Ct32 => Read32(x, y, tbp, tbw),
                    T8 or T4 => clut![ReadIndex(psm, x, y, tbp, tbw)],
                    _ => throw new NotSupportedException($"texture PSM 0x{psm:X}"),
                };
                int o = (y * width + x) * 4;
                rgba[o] = (byte)c;
                rgba[o + 1] = (byte)(c >> 8);
                rgba[o + 2] = (byte)(c >> 16);
                rgba[o + 3] = Alpha(c >> 24);
            }
        }
        return rgba;
    }

    public readonly record struct Transfer(int X, int Y, int Width, int Height, int Offset, int Length);

    public static List<Transfer> Transfers(ReadOnlySpan<byte> s)
    {
        List<Transfer> list = [];
        int p = 0, dx = 0, dy = 0, rw = 0, rh = 0;
        while (p + 16 <= s.Length)
        {
            ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);
            ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(s[(p + 8)..]);
            int nloop = (int)(lo & 0x7FFF), flg = (int)((lo >> 58) & 3), nreg = (int)((lo >> 60) & 15);
            p += 16;
            if (flg == 2)
            {
                if (p + nloop * 16 > s.Length) throw new InvalidDataException("GIF image packet overruns its data");
                list.Add(new Transfer(dx, dy, rw, rh, p, nloop * 16));
                p += nloop * 16;
                continue;
            }
            if (lo == 0 && hi == 0) break;
            if (flg != 0 || nreg != 1 || (hi & 0xF) != 0xE) throw new InvalidDataException($"unexpected GIF tag at 0x{p - 16:X}");
            for (int i = 0; i < nloop; i++, p += 16)
            {
                if (p + 16 > s.Length) throw new InvalidDataException("GIF A+D packet overruns its data");
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);
                switch ((int)(BinaryPrimitives.ReadUInt64LittleEndian(s[(p + 8)..]) & 0xFF))
                {
                    case 0x51: dx = (int)((v >> 32) & 0x7FF); dy = (int)((v >> 48) & 0x7FF); break;
                    case 0x52: rw = (int)(v & 0xFFF); rh = (int)((v >> 32) & 0xFFF); break;
                }
            }
        }
        return list;
    }
}

internal sealed record Ps2TexEntry(string Name, int Width, int Height, int Depth, uint RasterFormat, ulong Tex0, int PixelOffset, int PixelSize);

internal static class Ps2TexFile
{
    public const uint PlatformPs2 = 0x00325350;

    public static bool IsPs2Tex(ReadOnlySpan<byte> data)
    {
        if (!Rw.IsChunk(data, 0, Rw.TexDictionary) || !Rw.IsChunk(data, 28, Rw.TextureNative)) return false;
        return data.Length >= 56 && BinaryPrimitives.ReadUInt32LittleEndian(data[52..]) == PlatformPs2;
    }

    public static IReadOnlyList<Ps2TexEntry> Read(ReadOnlySpan<byte> data)
    {
        int size = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (size < 0 || size > data.Length - 12) throw new InvalidDataException("truncated texture dictionary");
        List<Ps2TexEntry> result = [];
        foreach (RwChunk c in Rw.Children(data, 12, 12 + size))
        {
            if (c.Type != Rw.TextureNative) continue;
            List<RwChunk> ch = Rw.Children(data, c);
            if (ch.Count < 4 || ch[0].Type != Rw.Struct || BinaryPrimitives.ReadUInt32LittleEndian(data[ch[0].Offset..]) != PlatformPs2)
                throw new InvalidDataException("texture is not a PS2 native texture");
            string name = Rw.CString(data.Slice(ch[1].Offset, ch[1].Size));
            List<RwChunk> inner = Rw.Children(data, ch[3]);
            if (inner.Count < 2 || inner[0].Size < 0x40) throw new InvalidDataException($"texture '{name}' has no raster");
            ReadOnlySpan<byte> info = data.Slice(inner[0].Offset, 0x40);
            int w = BinaryPrimitives.ReadInt32LittleEndian(info);
            int h = BinaryPrimitives.ReadInt32LittleEndian(info[4..]);
            int depth = BinaryPrimitives.ReadInt32LittleEndian(info[8..]);
            uint fmt = BinaryPrimitives.ReadUInt32LittleEndian(info[12..]);
            ulong tex0 = BinaryPrimitives.ReadUInt64LittleEndian(info[16..]);
            if (w is <= 0 or > 4096 || h is <= 0 or > 4096) throw new InvalidDataException($"texture '{name}' has a bad size");
            result.Add(new Ps2TexEntry(name, w, h, depth, fmt, tex0, inner[1].Offset, inner[1].Size));
        }
        return result;
    }

    public static byte[] Decode(Ps2TexEntry e, ReadOnlySpan<byte> file)
    {
        ReadOnlySpan<byte> px = file.Slice(e.PixelOffset, e.PixelSize);
        List<Ps2Gs.Transfer> t = Ps2Gs.Transfers(px);
        int tbp = (int)(e.Tex0 & 0x3FFF), tbw = (int)((e.Tex0 >> 14) & 0x3F), psm = (int)((e.Tex0 >> 20) & 0x3F);
        int cpsm = (int)((e.Tex0 >> 51) & 0xF);
        bool indexed = psm is Ps2Gs.T8 or Ps2Gs.T4;
        if (t.Count < (indexed ? 2 : 1)) throw new InvalidDataException($"texture '{e.Name}' has {t.Count} uploads");
        var gs = new Ps2Gs();
        Ps2Gs.Transfer level = t[0];
        int texBits = Bits(psm);
        long area = (long)level.Width * level.Height;
        if (area == 0 || area * 32 < (long)e.Width * e.Height * texBits)
            throw new InvalidDataException($"texture '{e.Name}' uploads {level.Width}x{level.Height} for {e.Width}x{e.Height}");
        int upBits = (int)((long)e.Width * e.Height * texBits / area);
        int dpsm = upBits switch { 32 => Ps2Gs.Ct32, 16 => Ps2Gs.Ct16, 8 => Ps2Gs.T8, 4 => Ps2Gs.T4, _ => throw new InvalidDataException($"texture '{e.Name}' uploads {upBits} bits per pixel") };
        int dbw = Math.Max(tbw * PageWidth(dpsm) / PageWidth(psm), 1);
        gs.Upload(px.Slice(level.Offset, level.Length), tbp, dbw, dpsm, level.X, level.Y, level.Width, level.Height);
        uint[]? clut = null;
        if (indexed)
        {
            int csm = (int)((e.Tex0 >> 55) & 1);
            if (cpsm != Ps2Gs.Ct32 || csm != 0) throw new NotSupportedException($"texture '{e.Name}' uses CLUT PSM 0x{cpsm:X} CSM{csm + 1}");
            Ps2Gs.Transfer c = t[^1];
            clut = Ps2Gs.Clut(psm, px.Slice(c.Offset, c.Length), c.Width);
        }
        return gs.ReadTexture(e.Tex0, e.Width, e.Height, clut);
    }

    private static int Bits(int psm) => psm switch
    {
        Ps2Gs.Ct32 => 32, Ps2Gs.Ct16 => 16, Ps2Gs.T8 => 8, Ps2Gs.T4 => 4,
        _ => throw new NotSupportedException($"texture PSM 0x{psm:X}"),
    };

    private static int PageWidth(int psm) => psm is Ps2Gs.T8 or Ps2Gs.T4 ? 128 : 64;

    public static string FormatName(Ps2TexEntry e) => (int)((e.Tex0 >> 20) & 0x3F) switch
    {
        Ps2Gs.T4 => "PSMT4 (4bpp indexed)",
        Ps2Gs.T8 => "PSMT8 (8bpp indexed)",
        Ps2Gs.Ct32 => "PSMCT32",
        Ps2Gs.Ct16 => "PSMCT16",
        int p => $"PSM 0x{p:X}",
    };
}
