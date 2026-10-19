using DWSSK.Formats.Core;
namespace DWSSK.Formats.Textures;
internal sealed record Ctpk(int Format, int Width, int Height, int Mips, string Name, int DataStart, int DataSize);
internal static class Pica
{
    private static readonly int[] s_bpp = [32, 24, 16, 16, 16, 16, 16, 8, 8, 8, 4, 4, 4, 8];
    public static readonly string[] Names =
        ["RGBA8", "RGB8", "RGBA5551", "RGB565", "RGBA4444", "LA8", "HILO8", "L8", "A8", "LA4", "L4", "A4", "ETC1", "ETC1A4"];
    private static readonly int[,] s_mod =
    {
        { 2, 8, -2, -8 }, { 5, 17, -5, -17 }, { 9, 29, -9, -29 }, { 13, 42, -13, -42 },
        { 18, 60, -18, -60 }, { 24, 80, -24, -80 }, { 33, 106, -33, -106 }, { 47, 183, -47, -183 },
    };
    public static int Bpp(int fmt)
    {
        return fmt >= 0 && fmt < s_bpp.Length ? s_bpp[fmt] : throw new InvalidDataException($"PICA format {fmt}");
    }
    public static int Size(int fmt, int w, int h)
    {
        return Bin.Align(w, 8) * Bin.Align(h, 8) * Bpp(fmt) / 8;
    }
    public static Ctpk ParseCtpk(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 4 || !blob[..4].SequenceEqual("CTPK"u8))
        {
            throw new InvalidDataException("not CTPK");
        }
        int count = Bin.U16(blob, 6, false);
        int texOff = (int)Bin.U32(blob, 8, false);
        if (count != 1)
        {
            throw new InvalidDataException($"CTPK with {count} textures");
        }
        int nameOff = (int)Bin.U32(blob, 0x20, false);
        int size = (int)Bin.U32(blob, 0x24, false);
        int dataOff = (int)Bin.U32(blob, 0x28, false);
        int fmt = (int)Bin.U32(blob, 0x2C, false);
        int w = Bin.U16(blob, 0x30, false);
        int h = Bin.U16(blob, 0x32, false);
        int mips = Bin.U8(blob, 0x34);
        string name = Bin.CString(blob, nameOff);
        int start = Math.Clamp(texOff + dataOff, 0, blob.Length);
        int end = Math.Clamp(texOff + dataOff + size, start, blob.Length);
        return new Ctpk(fmt, w, h, mips, name, start, end - start);
    }
    public static (int Offset, int Size) LevelSpan(Ctpk c, int level)
    {
        int off = 0;
        for (int i = 0; i < level; i++)
        {
            off += Size(c.Format, Math.Max(1, c.Width >> i), Math.Max(1, c.Height >> i));
        }
        return (off, Size(c.Format, Math.Max(1, c.Width >> level), Math.Max(1, c.Height >> level)));
    }
    private static void Etc1Block(ulong word, Span<byte> rgb)
    {
        long hi = (long)(word >> 32);
        long lo = (long)(word & 0xFFFFFFFF);
        long diff = (hi >> 1) & 1;
        long flip = hi & 1;
        int cw1 = (int)((hi >> 5) & 7);
        int cw2 = (int)((hi >> 2) & 7);
        Span<long> base1 = stackalloc long[3];
        Span<long> base2 = stackalloc long[3];
        int[] shifts = [24, 16, 8];
        for (int c = 0; c < 3; c++)
        {
            int sh = shifts[c];
            long v5 = (hi >> (sh + 3)) & 31;
            long d3 = (hi >> sh) & 7;
            if (d3 >= 4)
            {
                d3 -= 8;
            }
            long b5 = Math.Clamp(v5 + d3, 0, 31);
            long i1 = (hi >> (sh + 4)) & 15;
            long i2 = (hi >> sh) & 15;
            base1[c] = diff == 1 ? (v5 << 3) | (v5 >> 2) : i1 * 17;
            base2[c] = diff == 1 ? (b5 << 3) | (b5 >> 2) : i2 * 17;
        }
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                int i = (x * 4) + y;
                int idx = (int)((((lo >> (16 + i)) & 1) << 1) | ((lo >> i) & 1));
                bool second = flip == 1 ? y >= 2 : x >= 2;
                int cw = second ? cw2 : cw1;
                for (int c = 0; c < 3; c++)
                {
                    long b = second ? base2[c] : base1[c];
                    rgb[(((y * 4) + x) * 3) + c] = (byte)Math.Clamp(b + s_mod[cw, idx], 0, 255);
                }
            }
        }
    }
    public static byte[] Decode(int fmt, ReadOnlySpan<byte> data, int w, int h)
    {
        int pw = Bin.Align(w, 8);
        int ph = Bin.Align(h, 8);
        byte[] img = new byte[pw * ph * 4];
        if (fmt is 12 or 13)
        {
            int nb = pw * ph / 16;
            int bsz = fmt == 12 ? 8 : 16;
            if (data.Length < nb * bsz)
            {
                throw new InvalidDataException($"ETC1 data holds {data.Length} bytes, a {w}x{h} surface needs {nb * bsz}");
            }
            Span<byte> rgb = stackalloc byte[48];
            int tilesX = pw / 8;
            for (int k = 0; k < nb; k++)
            {
                ReadOnlySpan<byte> b = data.Slice(k * bsz, bsz);
                Etc1Block(Bin.U64(b, bsz - 8, false), rgb);
                ulong alpha = fmt == 13 ? Bin.U64(b, 0, false) : 0;
                int tile = k / 4;
                int sub = k % 4;
                int ty = tile / tilesX;
                int tx = tile % tilesX;
                int by = sub / 2;
                int bx = sub % 2;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int row = (ty * 8) + (by * 4) + y;
                        int col = (tx * 8) + (bx * 4) + x;
                        int o = ((row * pw) + col) * 4;
                        img[o] = rgb[((y * 4) + x) * 3];
                        img[o + 1] = rgb[(((y * 4) + x) * 3) + 1];
                        img[o + 2] = rgb[(((y * 4) + x) * 3) + 2];
                        img[o + 3] = fmt == 13 ? (byte)(((alpha >> (4 * ((x * 4) + y))) & 15) * 17) : (byte)255;
                    }
                }
            }
        }
        else
        {
            int bpp = Bpp(fmt);
            int n = pw * ph;
            if (data.Length < n * bpp / 8)
            {
                throw new InvalidDataException($"PICA data holds {data.Length} bytes, a {w}x{h} surface needs {n * bpp / 8}");
            }
            byte[] px = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                Texel(fmt, data, i, bpp, px);
            }
            (int[] mx, int[] my) = Pixels.MortonXY(8, 8);
            int tilesX = pw / 8;
            for (int t = 0; t < n / 64; t++)
            {
                int ty = t / tilesX;
                int tx = t % tilesX;
                for (int k = 0; k < 64; k++)
                {
                    int row = (ty * 8) + my[k];
                    int col = (tx * 8) + mx[k];
                    Buffer.BlockCopy(px, ((t * 64) + k) * 4, img, ((row * pw) + col) * 4, 4);
                }
            }
        }
        byte[] output = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int src = ph - 1 - (ph - h) - y;
            Buffer.BlockCopy(img, src * pw * 4, output, y * w * 4, w * 4);
        }
        return output;
    }
    private static void Texel(int fmt, ReadOnlySpan<byte> data, int i, int bpp, byte[] o)
    {
        uint v = bpp switch
        {
            4 => (uint)((data[i / 2] >> (i % 2 == 0 ? 0 : 4)) & 15),
            8 => data[i],
            16 => Bin.U16(data, i * 2, false),
            _ => 0,
        };
        switch (fmt)
        {
            case 0:
                Pixels.Put(o, i, data[(i * 4) + 3], data[(i * 4) + 2], data[(i * 4) + 1], data[i * 4]);
                break;
            case 1:
                Pixels.Put(o, i, data[(i * 3) + 2], data[(i * 3) + 1], data[i * 3], 255);
                break;
            case 2:
                Pixels.Rgba5551(v, o, i);
                break;
            case 3:
                Pixels.Rgb565(v, o, i);
                break;
            case 4:
                Pixels.Rgba4444(v, o, i);
                break;
            case 5:
                Pixels.Put(o, i, v >> 8, v >> 8, v >> 8, v & 255);
                break;
            case 6:
                Pixels.Put(o, i, v >> 8, v & 255, 0, 255);
                break;
            case 7:
                Pixels.Put(o, i, v, v, v, 255);
                break;
            case 8:
                Pixels.Put(o, i, 0, 0, 0, v);
                break;
            case 9:
                Pixels.Put(o, i, (v >> 4) * 17, (v >> 4) * 17, (v >> 4) * 17, (v & 15) * 17);
                break;
            case 10:
                Pixels.Put(o, i, v * 17, v * 17, v * 17, 255);
                break;
            case 11:
                Pixels.Put(o, i, 0, 0, 0, v * 17);
                break;
            default:
                throw new InvalidDataException($"PICA format {fmt}");
        }
    }
    public static byte[] Encode(int fmt, byte[] img, int w, int h)
    {
        int pw = Bin.Align(w, 8);
        int ph = Bin.Align(h, 8);
        byte[] flipped = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            int padRows = ph - h;
            int srcFlippedRow = y < padRows ? 0 : y - padRows;
            int srcRow = h - 1 - srcFlippedRow;
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(x, w - 1);
                Buffer.BlockCopy(img, ((srcRow * w) + sx) * 4, flipped, ((y * pw) + x) * 4, 4);
            }
        }
        int tilesX = pw / 8;
        int tiles = pw * ph / 64;
        if (fmt is 12 or 13)
        {
            int nb = tiles * 4;
            byte[][] blocks = new byte[nb][];
            for (int k = 0; k < nb; k++)
            {
                int tile = k / 4;
                int sub = k % 4;
                int ty = tile / tilesX;
                int tx = tile % tilesX;
                int by = sub / 2;
                int bx = sub % 2;
                byte[] blk = new byte[64];
                for (int y = 0; y < 4; y++)
                {
                    Buffer.BlockCopy(flipped, (((((ty * 8) + (by * 4) + y) * pw) + (tx * 8) + (bx * 4)) * 4), blk, y * 16, 16);
                }
                blocks[k] = blk;
            }
            ulong[] words = Etc1.Encode(blocks);
            ByteWriter output = new(nb * (fmt == 12 ? 8 : 16));
            for (int k = 0; k < nb; k++)
            {
                if (fmt == 13)
                {
                    ulong a = 0;
                    for (int x = 0; x < 4; x++)
                    {
                        for (int y = 0; y < 4; y++)
                        {
                            a |= (ulong)Pixels.Quant(blocks[k][(((y * 4) + x) * 4) + 3], 4) << (4 * ((x * 4) + y));
                        }
                    }
                    AddU64(output, a);
                }
                AddU64(output, words[k]);
            }
            return output.ToArray();
        }
        (int[] mx, int[] my) = Pixels.MortonXY(8, 8);
        byte[] p = new byte[pw * ph * 4];
        for (int t = 0; t < tiles; t++)
        {
            int ty = t / tilesX;
            int tx = t % tilesX;
            for (int k = 0; k < 64; k++)
            {
                Buffer.BlockCopy(flipped, ((((ty * 8) + my[k]) * pw) + (tx * 8) + mx[k]) * 4, p, ((t * 64) + k) * 4, 4);
            }
        }
        int n = pw * ph;
        ByteWriter o = new(n * 4);
        if (fmt is 10 or 11)
        {
            for (int i = 0; i < n; i += 2)
            {
                uint v0 = Pixels.Quant(fmt == 10 ? Pixels.Lum(p[i * 4], p[(i * 4) + 1], p[(i * 4) + 2]) : p[(i * 4) + 3], 4);
                int j = i + 1;
                uint v1 = Pixels.Quant(fmt == 10 ? Pixels.Lum(p[j * 4], p[(j * 4) + 1], p[(j * 4) + 2]) : p[(j * 4) + 3], 4);
                o.Add((byte)(v0 | (v1 << 4)));
            }
            return o.ToArray();
        }
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> c = p.AsSpan(i * 4, 4);
            switch (fmt)
            {
                case 0:
                    o.Add(c[3]);
                    o.Add(c[2]);
                    o.Add(c[1]);
                    o.Add(c[0]);
                    break;
                case 1:
                    o.Add(c[2]);
                    o.Add(c[1]);
                    o.Add(c[0]);
                    break;
                case 2:
                    o.U16(Pixels.EncRgba5551(c), false);
                    break;
                case 3:
                    o.U16(Pixels.EncRgb565(c), false);
                    break;
                case 4:
                    o.U16(Pixels.EncRgba4444(c), false);
                    break;
                case 5:
                    o.U16((Pixels.Lum(c[0], c[1], c[2]) << 8) | c[3], false);
                    break;
                case 6:
                    o.U16(((uint)c[0] << 8) | c[1], false);
                    break;
                case 7:
                    o.Add((byte)Pixels.Lum(c[0], c[1], c[2]));
                    break;
                case 8:
                    o.Add(c[3]);
                    break;
                case 9:
                    o.Add((byte)((Pixels.Quant(Pixels.Lum(c[0], c[1], c[2]), 4) << 4) | Pixels.Quant(c[3], 4)));
                    break;
                default:
                    throw new InvalidDataException($"PICA format {fmt}");
            }
        }
        return o.ToArray();
    }
    private static void AddU64(ByteWriter o, ulong v)
    {
        for (int k = 0; k < 8; k++)
        {
            o.Add((byte)(v >> (8 * k)));
        }
    }
}
internal static class Etc1
{
    private static readonly int[,] s_mod =
    {
        { 2, 8, -2, -8 }, { 5, 17, -5, -17 }, { 9, 29, -9, -29 }, { 13, 42, -13, -42 },
        { 18, 60, -18, -60 }, { 24, 80, -24, -80 }, { 33, 106, -33, -106 }, { 47, 183, -47, -183 },
    };
    public static ulong[] Encode(byte[][] blocks)
    {
        ulong[] output = new ulong[blocks.Length];
        Span<int> pxIdx = stackalloc int[8];
        for (int b = 0; b < blocks.Length; b++)
        {
            byte[] px = blocks[b];
            long bestErr = long.MaxValue;
            long bestHi = 0;
            long bestLo = 0;
            for (int flip = 0; flip < 2; flip++)
            {
                int[][] members = new int[2][];
                for (int s = 0; s < 2; s++)
                {
                    List<int> list = [];
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            bool second = flip == 1 ? y >= 2 : x >= 2;
                            if (second == (s == 1))
                            {
                                list.Add((y * 4) + x);
                            }
                        }
                    }
                    members[s] = [.. list];
                }
                long[][] q4 = new long[2][];
                long[][] q5 = new long[2][];
                for (int s = 0; s < 2; s++)
                {
                    q4[s] = new long[3];
                    q5[s] = new long[3];
                    for (int c = 0; c < 3; c++)
                    {
                        long sum = 0;
                        foreach (int k in members[s])
                        {
                            sum += px[(k * 4) + c];
                        }
                        double avg = sum / 8.0;
                        q4[s][c] = (long)Math.Clamp(Math.Round(avg * 15 / 255, MidpointRounding.ToEven), 0, 15);
                        q5[s][c] = (long)Math.Clamp(Math.Round(avg * 31 / 255, MidpointRounding.ToEven), 0, 31);
                    }
                }
                long[] d = [q5[1][0] - q5[0][0], q5[1][1] - q5[0][1], q5[1][2] - q5[0][2]];
                bool diffOk = d.All(v => v is >= -4 and <= 3);
                for (int diff = 0; diff < 2; diff++)
                {
                    long[][] bases = new long[2][];
                    for (int s = 0; s < 2; s++)
                    {
                        bases[s] = new long[3];
                        for (int c = 0; c < 3; c++)
                        {
                            bases[s][c] = diff == 1 ? (q5[s][c] << 3) | (q5[s][c] >> 2) : q4[s][c] * 17;
                        }
                    }
                    long err = 0;
                    int[] cws = new int[2];
                    int[][] idxs = new int[2][];
                    for (int s = 0; s < 2; s++)
                    {
                        long bestCwErr = long.MaxValue;
                        int bestCw = 0;
                        int[] bestPick = new int[8];
                        for (int cw = 0; cw < 8; cw++)
                        {
                            long cwErr = 0;
                            for (int m = 0; m < 8; m++)
                            {
                                int k = members[s][m];
                                long best = long.MaxValue;
                                int pick = 0;
                                for (int j = 0; j < 4; j++)
                                {
                                    long e = 0;
                                    for (int c = 0; c < 3; c++)
                                    {
                                        long cand = Math.Clamp(bases[s][c] + s_mod[cw, j], 0, 255);
                                        long dv = px[(k * 4) + c] - cand;
                                        e += dv * dv;
                                    }
                                    if (e < best)
                                    {
                                        best = e;
                                        pick = j;
                                    }
                                }
                                pxIdx[m] = pick;
                                cwErr += best;
                            }
                            if (cwErr < bestCwErr)
                            {
                                bestCwErr = cwErr;
                                bestCw = cw;
                                pxIdx.CopyTo(bestPick);
                            }
                        }
                        err += bestCwErr;
                        cws[s] = bestCw;
                        idxs[s] = bestPick;
                    }
                    long hi;
                    if (diff == 1)
                    {
                        if (!diffOk)
                        {
                            err = long.MaxValue;
                        }
                        hi = (q5[0][0] << 27) | ((d[0] & 7) << 24) | (q5[0][1] << 19) | ((d[1] & 7) << 16)
                            | (q5[0][2] << 11) | ((d[2] & 7) << 8);
                    }
                    else
                    {
                        hi = (q4[0][0] << 28) | (q4[1][0] << 24) | (q4[0][1] << 20) | (q4[1][1] << 16)
                            | (q4[0][2] << 12) | (q4[1][2] << 8);
                    }
                    hi |= ((long)cws[0] << 5) | ((long)cws[1] << 2) | ((long)diff << 1) | (long)flip;
                    long lo = 0;
                    for (int s = 0; s < 2; s++)
                    {
                        for (int m = 0; m < 8; m++)
                        {
                            int k = members[s][m];
                            int y = k / 4;
                            int x = k % 4;
                            int i = (x * 4) + y;
                            long v = idxs[s][m];
                            lo |= ((v >> 1) << (16 + i)) | ((v & 1) << i);
                        }
                    }
                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestHi = hi;
                        bestLo = lo;
                    }
                }
            }
            output[b] = ((ulong)bestHi << 32) | (ulong)bestLo;
        }
        return output;
    }
}
