using DWSSK.Formats.Core;

namespace DWSSK.Formats.Textures;

internal sealed class Htx
{
    public const uint Magic = 0xFC000001;

    private Htx(byte[] blob, int width, int height, int mips, int format, int flag, bool big, int dataOffset)
    {
        Blob = blob;
        Width = width;
        Height = height;
        Mips = mips;
        Format = format;
        Flag = flag;
        BigEndian = big;
        DataOffset = dataOffset;
    }

    public byte[] Blob { get; }
    public int Width { get; }
    public int Height { get; }
    public int Mips { get; }
    public int Format { get; }
    public int Flag { get; }
    public bool BigEndian { get; }
    public int DataOffset { get; }
    private bool Linear => (Flag & 1) == 0;
    public ReadOnlySpan<byte> Data => Bin.Slice(Blob, DataOffset, Blob.Length);

    public static bool IsHtx(ReadOnlySpan<byte> b)
    {
        return b.Length >= 0x20 && Bin.U32(b, 0, b[0] == 0xFC) == Magic;
    }

    public static Htx Parse(byte[] blob)
    {
        if (blob.Length < 0x20)
        {
            throw new InvalidDataException("not an HTX file: shorter than its header");
        }
        bool be = blob[0] == 0xFC;
        uint[] f = Bin.U32s(blob, 0, 8, be);
        return f[0] != Magic
            ? throw new InvalidDataException("not an HTX file")
            : new Htx(blob, (int)f[1], (int)f[2], (int)f[4], (int)f[5], (int)f[6], be, (int)f[7]);
    }

    private static (int Block, int Bytes) BlockOf(int fmt)
    {
        return fmt switch
        {
            0 => (1, 4),
            2 or 3 or 10 => (1, 2),
            5 => (4, 8),
            6 or 7 => (4, 16),
            _ => throw new InvalidDataException($"HTX format {fmt} has no block layout"),
        };
    }

    public static string FormatName(int fmt)
    {
        return fmt switch
        {
            0 => "RGBA8",
            2 => "16-bit",
            3 => "ARGB4444",
            5 => "DXT1",
            6 => "DXT3",
            7 => "DXT5",
            8 => "CTPK",
            10 => "LA8",
            _ => $"format {fmt}",
        };
    }

    public string Describe(Platform platform)
    {
        if (platform == Platform.N3ds && Format == 8)
        {
            Ctpk c = Pica.ParseCtpk(Data);
            return c.Format >= 0 && c.Format < Pica.Names.Length ? $"CTPK {Pica.Names[c.Format]}" : $"CTPK format {c.Format}";
        }
        return platform == Platform.Wii && Format == 5 ? "CMPR (DXT1)" : FormatName(Format);
    }

    public (int Width, int Height, int Mips) Dimensions(Platform platform)
    {
        if (platform == Platform.N3ds)
        {
            Ctpk c = Pica.ParseCtpk(Data);
            return (c.Width, c.Height, c.Mips);
        }
        return (Width, Height, Mips);
    }

    private static int BcN(int fmt)
    {
        return fmt switch
        {
            5 => 1,
            6 => 2,
            7 => 3,
            _ => throw new InvalidDataException($"HTX format {fmt} is not block compressed"),
        };
    }

    public List<(int Offset, int Width, int Height, int Pitch)> X360Mips()
    {
        (int bs, int bpb) = BlockOf(Format);
        List<(int, int, int, int)> output = [];
        int off = 0;
        for (int i = 0; i < Mips; i++)
        {
            int w = Math.Max(1, Width >> i);
            int h = Math.Max(1, Height >> i);
            int wb = Bin.CeilDiv(w, bs);
            int hb = Bin.CeilDiv(h, bs);
            int pitch = Math.Max(256, Bin.Align(wb, 32) * bpb);
            output.Add((off, w, h, pitch));
            off += pitch * (Mips == 1 && Height < 32 ? hb : Bin.Align(hb, 32));
        }
        return output;
    }

    public List<(int Offset, int Size, int Width, int Height)> LevelSizes(Platform platform)
    {
        List<(int, int, int, int)> output = [];
        if (platform == Platform.Xbox360)
        {
            (int bs, _) = BlockOf(Format);
            foreach ((int off, int w, int h, int pitch) in X360Mips())
            {
                output.Add((off, pitch * Bin.CeilDiv(h, bs), w, h));
            }
            return output;
        }
        int o = 0;
        for (int i = 0; i < Mips; i++)
        {
            int w = Math.Max(1, Width >> i);
            int h = Math.Max(1, Height >> i);
            int size;
            if (Format is 5 or 6 or 7)
            {
                size = platform == Platform.Wii ? Bin.Align(w, 8) * Bin.Align(h, 8) / 2 : Bcn.Size(w, h, BcN(Format));
            }
            else
            {
                int bpp = BlockOf(Format).Bytes;
                size = (platform == Platform.Wii ? Bin.Align(w, 4) * Bin.Align(h, 4) : w * h) * bpp;
            }
            output.Add((o, size, w, h));
            o += size;
        }
        return output;
    }

    public int MipCount(Platform platform)
    {
        return platform == Platform.N3ds ? Pica.ParseCtpk(Data).Mips : Mips;
    }

    public (byte[] Rgba, int Width, int Height) DecodeLevel(Platform platform, int level = 0)
    {
        if (platform == Platform.N3ds)
        {
            ReadOnlySpan<byte> data = Data;
            Ctpk c = Pica.ParseCtpk(data);
            int off = 0;
            for (int i = 0; i < level; i++)
            {
                off += Pica.Size(c.Format, Math.Max(1, c.Width >> i), Math.Max(1, c.Height >> i));
            }
            int w = Math.Max(1, c.Width >> level);
            int h = Math.Max(1, c.Height >> level);
            ReadOnlySpan<byte> tex = data.Slice(c.DataStart, c.DataSize);
            return (Pica.Decode(c.Format, Bin.Slice(tex, off, tex.Length), w, h), w, h);
        }
        if (platform == Platform.Xbox360)
        {
            return DecodeX360(level);
        }
        (int o, int size, int lw, int lh) = LevelSizes(platform)[level];
        ReadOnlySpan<byte> raw = Bin.Slice(Data, o, o + size);
        if (platform == Platform.Wii)
        {
            return (Gx.DecodeHtx(Format, raw, lw, lh), lw, lh);
        }
        if (Format is 5 or 6 or 7)
        {
            return (Bcn.Decode(raw, lw, lh, BcN(Format)), lw, lh);
        }
        if (Format == 0)
        {
            byte[] px = Linear ? Need(raw, lw * lh * 4) : Pixels.Unswizzle(Need(raw, lw * lh * 4), 4, lw, lh);
            for (int i = 0; i < px.Length; i += 4)
            {
                (px[i], px[i + 2]) = (px[i + 2], px[i]);
            }
            return (px, lw, lh);
        }
        byte[] units = new byte[lw * lh * 4];
        Need(raw, lw * lh * 2);
        for (int i = 0; i < lw * lh; i++)
        {
            Color16(Format, Bin.U16(raw, i * 2, true), units, i);
        }
        return (Linear ? units : Pixels.Unswizzle(units, 4, lw, lh), lw, lh);
    }

    private static byte[] Need(ReadOnlySpan<byte> raw, int n)
    {
        return raw.Length < n
            ? throw new InvalidDataException($"texture level holds {raw.Length} bytes, needs {n}")
            : raw[..n].ToArray();
    }

    private static void Color16(int fmt, uint v, byte[] o, int i)
    {
        switch (fmt)
        {
            case 2:
                Pixels.Argb1555(v, o, i);
                break;
            case 3:
                Pixels.Argb4444(v, o, i);
                break;
            case 10:
                Pixels.La8(v, o, i);
                break;
            default:
                throw new InvalidDataException($"HTX format {fmt} is not a 16-bit format");
        }
    }

    private (byte[], int, int) DecodeX360(int level)
    {
        (int off, int w, int h, int pitch) = X360Mips()[level];
        (int bs, int bpb) = BlockOf(Format);
        int wb = Bin.CeilDiv(w, bs);
        int hb = Bin.CeilDiv(h, bs);
        ReadOnlySpan<byte> data = Data;
        Bin.Need(data, off, pitch * hb);
        byte[] rows = new byte[hb * wb * bpb];
        for (int r = 0; r < hb; r++)
        {
            data.Slice(off + (r * pitch), wb * bpb).CopyTo(rows.AsSpan(r * wb * bpb));
        }
        if (Format is 5 or 6 or 7)
        {
            Swap16(rows);
            return (Bcn.Decode(rows, w, h, Format - 4), w, h);
        }
        byte[] output = new byte[w * h * 4];
        if (Format == 0)
        {
            for (int i = 0; i < w * h; i++)
            {
                Pixels.Put(output, i, rows[(i * 4) + 1], rows[(i * 4) + 2], rows[(i * 4) + 3], rows[i * 4]);
            }
            return (output, w, h);
        }
        for (int i = 0; i < w * h; i++)
        {
            Color16(Format, Bin.U16(rows, i * 2, true), output, i);
        }
        return (output, w, h);
    }

    public static void Swap16(byte[] a)
    {
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            (a[i], a[i + 1]) = (a[i + 1], a[i]);
        }
    }

    public byte[] EncodeLevel(byte[] img, int w, int h, Platform platform, int level)
    {
        int f = Format;
        if (f is 5 or 6 or 7)
        {
            return BcToLevel(Bcn.Encode(img, w, h, BcN(f)), platform, level);
        }
        ByteWriter o = new(w * h * 4);
        if (platform == Platform.PS3)
        {
            if (f == 0)
            {
                byte[] bgra = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    Pixels.Put(bgra, i, img[(i * 4) + 2], img[(i * 4) + 1], img[i * 4], img[(i * 4) + 3]);
                }
                return Linear ? bgra : Pixels.Swizzle(bgra, 4, w, h);
            }
            byte[] v = new byte[w * h * 2];
            for (int i = 0; i < w * h; i++)
            {
                Bin.PutU16(v, i * 2, Encode16(f, img.AsSpan(i * 4, 4)), true);
            }
            return Linear ? v : Pixels.Swizzle(v, 2, w, h);
        }
        if (platform == Platform.Wii)
        {
            if (f == 10)
            {
                byte[] al = new byte[w * h * 2];
                for (int i = 0; i < w * h; i++)
                {
                    al[i * 2] = img[(i * 4) + 3];
                    al[(i * 2) + 1] = (byte)Pixels.Lum(img[i * 4], img[(i * 4) + 1], img[(i * 4) + 2]);
                }
                return Pixels.Tile(al, 2, w, h, 4, 4);
            }
            if (f == 0)
            {
                byte[] tl = Pixels.Tile(img, 4, w, h, 4, 4);
                int tiles = tl.Length / 64;
                for (int t = 0; t < tiles; t++)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        int p = (t * 64) + (k * 4);
                        o.Add(tl[p + 3]);
                        o.Add(tl[p]);
                    }
                    for (int k = 0; k < 16; k++)
                    {
                        int p = (t * 64) + (k * 4);
                        o.Add(tl[p + 1]);
                        o.Add(tl[p + 2]);
                    }
                }
                return o.ToArray();
            }
        }
        if (platform == Platform.Xbox360)
        {
            if (f == 0)
            {
                for (int i = 0; i < w * h; i++)
                {
                    o.Add(img[(i * 4) + 3]);
                    o.Add(img[i * 4]);
                    o.Add(img[(i * 4) + 1]);
                    o.Add(img[(i * 4) + 2]);
                }
                return o.ToArray();
            }
            for (int i = 0; i < w * h; i++)
            {
                o.U16(Encode16(f, img.AsSpan(i * 4, 4)), true);
            }
            return o.ToArray();
        }
        throw new InvalidDataException($"{platform.Name()} format {f}");
    }

    private static uint Encode16(int fmt, ReadOnlySpan<byte> p)
    {
        return fmt switch
        {
            3 => Pixels.EncArgb4444(p),
            2 => Pixels.EncArgb1555(p),
            10 => Pixels.EncLa8(p),
            _ => throw new InvalidDataException($"HTX format {fmt} is not a 16-bit format"),
        };
    }

    public byte[] BcToLevel(byte[] bc, Platform platform, int level)
    {
        int w = Math.Max(1, Width >> level);
        int h = Math.Max(1, Height >> level);
        if (platform == Platform.Wii)
        {
            return Gx.Bc1ToCmpr(bc, w, h);
        }
        if (platform == Platform.Xbox360)
        {
            byte[] c = (byte[])bc.Clone();
            Swap16(c);
            return c;
        }
        return bc;
    }

    public void WriteLevel(byte[] data, Platform platform, int level, byte[] payload)
    {
        if (platform == Platform.Xbox360)
        {
            (int off, int w, int h, int pitch) = X360Mips()[level];
            (int bs, int bpb) = BlockOf(Format);
            int row = Bin.CeilDiv(w, bs) * bpb;
            for (int r = 0; r < Bin.CeilDiv(h, bs); r++)
            {
                Span<byte> dst = data.AsSpan(off + (r * pitch), row);
                Bin.Slice(payload, r * row, (r + 1) * row).CopyTo(dst);
            }
            return;
        }
        (int o, int size, _, _) = LevelSizes(platform)[level];
        if (payload.Length != size)
        {
            throw new InvalidDataException($"level {level}: {payload.Length} bytes, expected {size}");
        }
        payload.CopyTo(data, o);
    }

    public byte[] ReadLevel(Platform platform, int level)
    {
        if (platform == Platform.Xbox360)
        {
            (int off, int w, int h, int pitch) = X360Mips()[level];
            (int bs, int bpb) = BlockOf(Format);
            int row = Bin.CeilDiv(w, bs) * bpb;
            ByteWriter o = new(row * Bin.CeilDiv(h, bs));
            for (int r = 0; r < Bin.CeilDiv(h, bs); r++)
            {
                o.Add(Bin.Slice(Data, off + (r * pitch), off + (r * pitch) + row));
            }
            return o.ToArray();
        }
        (int at, int size, _, _) = LevelSizes(platform)[level];
        return Bin.Slice(Data, at, at + size).ToArray();
    }

    public bool IsLossless(Platform platform)
    {
        return platform == Platform.N3ds ? Pica.ParseCtpk(Data).Format is not (12 or 13) : Format is not (5 or 6 or 7);
    }

    public static byte[] FromImage(byte[] blob, Platform platform, byte[] px, int width, int height)
    {
        Htx t = Parse(blob);
        (byte[] current, int cw, int ch) = t.DecodeLevel(platform, 0);
        if (cw == width && ch == height && current.AsSpan().SequenceEqual(px))
        {
            return blob;
        }
        byte[] head = blob[..t.DataOffset];
        byte[] data = t.Data.ToArray();
        if (platform == Platform.N3ds)
        {
            Ctpk c = Pica.ParseCtpk(data);
            if (height != c.Height || width != c.Width)
            {
                throw new InvalidDataException($"PNG is {width}x{height}, texture is {c.Width}x{c.Height}");
            }
            byte[] cdata = data.AsSpan(c.DataStart, c.DataSize).ToArray();
            byte[] tex = (byte[])cdata.Clone();
            foreach ((int lv, (byte[] im, int iw, int ih)) in Pixels.MipChain(px, width, height, c.Mips).Index())
            {
                (int off, int size) = Pica.LevelSpan(c, lv);
                Splice(tex, off, size, Pica.Encode(c.Format, im, iw, ih));
            }
            int start = data.AsSpan().IndexOf(cdata);
            ByteWriter o = new(blob.Length);
            o.Add(head);
            o.Add(data.AsSpan(0, start));
            o.Add(tex);
            o.Add(data.AsSpan(start + cdata.Length));
            return o.ToArray();
        }
        if (height != t.Height || width != t.Width)
        {
            throw new InvalidDataException($"PNG is {width}x{height}, texture is {t.Width}x{t.Height}");
        }
        foreach ((int lv, (byte[] im, int iw, int ih)) in Pixels.MipChain(px, width, height, t.Mips).Index())
        {
            t.WriteLevel(data, platform, lv, t.EncodeLevel(im, iw, ih, platform, lv));
        }
        return [.. head, .. data];
    }

    private static void Splice(byte[] tex, int off, int size, byte[] enc)
    {
        if (enc.Length != size)
        {
            throw new InvalidDataException($"encoded level is {enc.Length} bytes, its slot holds {size}");
        }
        if (off + size > tex.Length)
        {
            throw new InvalidDataException("encoded level runs past the end of the texture data");
        }
        enc.CopyTo(tex, off);
    }
}

internal static class Gx
{
    public static byte[] CmprToBc1(ReadOnlySpan<byte> data, int w, int h)
    {
        int bw = Bin.CeilDiv(w, 8) * 2;
        int bh = Bin.CeilDiv(h, 8) * 2;
        Bin.Need(data, 0, bw * bh * 8);
        int ow = Bin.CeilDiv(w, 4);
        int oh = Bin.CeilDiv(h, 4);
        byte[] output = new byte[ow * oh * 8];
        for (int by = 0; by < oh; by++)
        {
            for (int bx = 0; bx < ow; bx++)
            {
                int tile = ((by / 2) * (bw / 2)) + (bx / 2);
                int src = ((tile * 4) + ((by % 2) * 2) + (bx % 2)) * 8;
                int dst = ((by * ow) + bx) * 8;
                output[dst] = data[src + 1];
                output[dst + 1] = data[src];
                output[dst + 2] = data[src + 3];
                output[dst + 3] = data[src + 2];
                for (int k = 4; k < 8; k++)
                {
                    output[dst + k] = FlipIndices(data[src + k]);
                }
            }
        }
        return output;
    }

    private static byte FlipIndices(byte idx)
    {
        return (byte)(((idx & 3) << 6) | ((idx & 12) << 2) | ((idx & 48) >> 2) | (idx >> 6));
    }

    public static byte[] Bc1ToCmpr(byte[] bc1, int w, int h)
    {
        int bw = Bin.CeilDiv(w, 4);
        int bh = Bin.CeilDiv(h, 4);
        int pbw = Bin.Align(bw, 2);
        int pbh = Bin.Align(bh, 2);
        byte[] blk = new byte[pbh * pbw * 8];
        for (int y = 0; y < bh; y++)
        {
            Buffer.BlockCopy(bc1, y * bw * 8, blk, y * pbw * 8, bw * 8);
        }
        for (int b = 0; b < pbh * pbw; b++)
        {
            int p = b * 8;
            (blk[p], blk[p + 1], blk[p + 2], blk[p + 3]) = (blk[p + 1], blk[p], blk[p + 3], blk[p + 2]);
            for (int k = 4; k < 8; k++)
            {
                blk[p + k] = FlipIndices(blk[p + k]);
            }
        }
        byte[] output = new byte[blk.Length];
        int o = 0;
        for (int ty = 0; ty < pbh / 2; ty++)
        {
            for (int tx = 0; tx < pbw / 2; tx++)
            {
                for (int sy = 0; sy < 2; sy++)
                {
                    for (int sx = 0; sx < 2; sx++)
                    {
                        Buffer.BlockCopy(blk, ((((ty * 2) + sy) * pbw) + (tx * 2) + sx) * 8, output, o, 8);
                        o += 8;
                    }
                }
            }
        }
        return output;
    }

    public static byte[] DecodeHtx(int fmt, ReadOnlySpan<byte> data, int w, int h)
    {
        switch (fmt)
        {
            case 5:
                return Bcn.Decode(CmprToBc1(data, w, h), w, h, 1);
            case 10:
            {
                int n = Bin.CeilDiv(w, 4) * 4 * Bin.CeilDiv(h, 4) * 4;
                Bin.Need(data, 0, n * 2);
                byte[] units = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    byte a = data[i * 2];
                    byte l = data[(i * 2) + 1];
                    Pixels.Put(units, i, l, l, l, a);
                }
                return Pixels.Untile(units, 4, w, h, 4, 4);
            }
            case 0:
            {
                int n = Bin.CeilDiv(w, 4) * Bin.CeilDiv(h, 4);
                Bin.Need(data, 0, n * 64);
                byte[] units = new byte[n * 16 * 4];
                for (int t = 0; t < n; t++)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        int ar = (t * 64) + (k * 2);
                        int gb = (t * 64) + 32 + (k * 2);
                        Pixels.Put(units, (t * 16) + k, data[ar + 1], data[gb], data[gb + 1], data[ar]);
                    }
                }
                return Pixels.Untile(units, 4, w, h, 4, 4);
            }
            default:
                throw new InvalidDataException($"Wii HTX format {fmt}");
        }
    }
}
