using System.Buffers.Binary;

namespace DWSSK.Formats.Video;

internal sealed class LsbReader(byte[] data, int start, int length)
{
    private readonly int _end = start + length;

    public int Size { get; } = length * 8;
    public int Pos { get; set; }
    public int Left => Size - Pos;

    public uint Peek(int n)
    {
        if (n <= 0)
        {
            return 0;
        }
        int at = start + (Pos >> 3);
        ulong w = 0;
        if (at + 8 <= _end)
        {
            w = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at, 8));
        }
        else
        {
            for (int i = 7; i >= 0; i--)
            {
                w = (w << 8) | (at + i < _end ? data[at + i] : 0u);
            }
        }
        return (uint)((w >> (Pos & 7)) & ((1UL << n) - 1));
    }

    public uint Read(int n)
    {
        uint v = Peek(n);
        Pos += n;
        return v;
    }

    public int Bit()
    {
        return (int)Read(1);
    }
}

internal sealed record BinkTrack(int Rate, int Channels, bool Dct, uint Id);

internal sealed class BinkFile
{
    private BinkFile(byte[] data, char revision, int frames, int width, int height, int rateNum, int rateDen, uint flags, List<BinkTrack> tracks, int[] offsets, bool[] keys)
    {
        Data = data;
        Revision = revision;
        Frames = frames;
        Width = width;
        Height = height;
        RateNumerator = rateNum;
        RateDenominator = rateDen;
        Flags = flags;
        Tracks = tracks;
        Offsets = offsets;
        Keyframes = keys;
    }

    public byte[] Data { get; }
    public char Revision { get; }
    public int Frames { get; }
    public int Width { get; }
    public int Height { get; }
    public int RateNumerator { get; }
    public int RateDenominator { get; }
    public uint Flags { get; }
    public IReadOnlyList<BinkTrack> Tracks { get; }
    public int[] Offsets { get; }
    public bool[] Keyframes { get; }
    public bool HasAlpha => (Flags & 0x00100000) != 0;

    public static bool IsBink(ReadOnlySpan<byte> b)
    {
        return b.Length >= 44 && b[..3].SequenceEqual("BIK"u8) && b[3] is (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'k';
    }

    public static BinkFile Read(byte[] b)
    {
        if (!IsBink(b))
        {
            throw new InvalidDataException(b.Length >= 4 && b.AsSpan(0, 3).SequenceEqual("BIK"u8)
                ? $"Bink revision '{(char)b[3]}' is not supported"
                : "not a Bink video");
        }
        char revision = (char)b[3];
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) + 8;
        int frames = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        int width = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20));
        int height = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24));
        int rateNum = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(28));
        int rateDen = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(32));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
        int trackCount = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(40));
        if (frames is <= 0 or > 1000000 || width is <= 0 or > 7680 || height is <= 0 or > 4800 || rateNum <= 0 || rateDen <= 0 || trackCount is < 0 or > 256)
        {
            throw new InvalidDataException("the Bink header is damaged");
        }
        int at = 44 + (revision == 'k' ? 4 : 0);
        at += 4 * trackCount;
        List<BinkTrack> tracks = [];
        for (int i = 0; i < trackCount; i++)
        {
            int rate = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            int tflags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 2));
            tracks.Add(new BinkTrack(rate, (tflags & 0x2000) != 0 ? 2 : 1, (tflags & 0x1000) != 0, 0));
            at += 4;
        }
        for (int i = 0; i < trackCount; i++)
        {
            tracks[i] = tracks[i] with { Id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) };
            at += 4;
        }
        int[] offsets = new int[frames + 1];
        bool[] keys = new bool[frames];
        for (int i = 0; i <= frames; i++)
        {
            uint v = i == frames ? fileSize : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + (4 * i)));
            offsets[i] = (int)(v & ~1u);
            if (i < frames)
            {
                keys[i] = i == 0 || (v & 1) != 0;
            }
            if (i > 0 && (offsets[i] <= offsets[i - 1] || offsets[i] > b.Length))
            {
                throw new InvalidDataException($"the Bink frame index is damaged at frame {i}");
            }
        }
        return new BinkFile(b, revision, frames, width, height, rateNum, rateDen, flags, tracks, offsets, keys);
    }

    public (int Offset, int Length) Audio(int frame, int track)
    {
        int at = Offsets[frame];
        int end = Offsets[frame + 1];
        for (int t = 0; t < Tracks.Count; t++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));
            if (size > end - at - 4)
            {
                throw new InvalidDataException($"frame {frame}: the audio size runs past the frame");
            }
            if (t == track)
            {
                return (at + 4, size);
            }
            at += 4 + size;
        }
        return (at, 0);
    }

    public (int Offset, int Length) Video(int frame)
    {
        int at = Offsets[frame];
        for (int t = 0; t < Tracks.Count; t++)
        {
            at += 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));
        }
        return (at, Offsets[frame + 1] - at);
    }
}

internal sealed class BinkVideo
{
    private const int SrcBlockTypes = 0;
    private const int SrcSubBlockTypes = 1;
    private const int SrcColors = 2;
    private const int SrcPattern = 3;
    private const int SrcXOff = 4;
    private const int SrcYOff = 5;
    private const int SrcIntraDc = 6;
    private const int SrcInterDc = 7;
    private const int SrcRun = 8;
    private const int Sources = 9;

    private static readonly (int[] Table, int Bits)[] s_trees = [.. Enumerable.Range(0, 16).Select(Tree)];
    private static readonly int[] s_rleLens = [4, 8, 12, 32];

    private sealed class Bundle
    {
        public int Len;
        public int Vlc;
        public readonly byte[] Syms = new byte[16];
        public byte[] Bytes = [];
        public short[] Shorts = [];
        public int CurDec;
        public int CurPtr;
    }

    private readonly BinkFile _file;
    private readonly Bundle[] _bundles = [.. Enumerable.Range(0, Sources).Select(_ => new Bundle())];
    private readonly (int Vlc, byte[] Syms)[] _colHigh = [.. Enumerable.Range(0, 16).Select(_ => (0, new byte[16]))];
    private int _colLastVal;
    private byte[][] _cur;
    private byte[][] _last;
    private bool _haveLast;
    private readonly int[] _strides;
    private readonly int[] _rows;
    private readonly int[] _dct = new int[64];
    private readonly int[] _temp = new int[64];
    private readonly short[] _residue = new short[64];
    private readonly byte[] _ublock = new byte[64];
    private readonly int[] _coefIdx = new int[64];

    public BinkVideo(BinkFile file)
    {
        _file = file;
        int bw = (file.Width + 7) >> 3;
        int bh = (file.Height + 7) >> 3;
        int cbw = (file.Width + 15) >> 4;
        int cbh = (file.Height + 15) >> 4;
        _strides = [bw * 8, cbw * 8, cbw * 8, bw * 8];
        _rows = [bh * 8, cbh * 8, cbh * 8, bh * 8];
        _cur = Planes();
        _last = Planes();
        int blocks = bw * bh;
        foreach (Bundle b in _bundles)
        {
            b.Bytes = new byte[blocks * 64];
        }
        _bundles[SrcIntraDc].Shorts = new short[blocks * 32];
        _bundles[SrcInterDc].Shorts = new short[blocks * 32];
    }

    private byte[][] Planes()
    {
        return [.. Enumerable.Range(0, _file.HasAlpha ? 4 : 3).Select(p => new byte[_strides[p] * _rows[p]])];
    }

    public byte[][] Frame => _last;
    public int[] Strides => _strides;

    private static (int[] Table, int Bits) Tree(int n)
    {
        int bits = BinkTables.TreeLens[(n * 16) + 15];
        int[] table = new int[1 << bits];
        for (int s = 0; s < 16; s++)
        {
            int len = BinkTables.TreeLens[(n * 16) + s];
            int code = BinkTables.TreeBits[(n * 16) + s];
            for (int i = code; i < table.Length; i += 1 << len)
            {
                table[i] = s | (len << 8);
            }
        }
        return (table, bits);
    }

    private static int Huff(LsbReader gb, int vlc, byte[] syms)
    {
        (int[] table, int bits) = s_trees[vlc];
        int e = table[gb.Peek(bits)];
        gb.Pos += e >> 8;
        return syms[e & 0xFF];
    }

    private static void ReadTree(LsbReader gb, ref int vlc, byte[] syms)
    {
        if (gb.Left < 4)
        {
            throw new InvalidDataException("the Bink tree runs past the frame");
        }
        vlc = (int)gb.Read(4);
        if (vlc == 0)
        {
            for (int i = 0; i < 16; i++)
            {
                syms[i] = (byte)i;
            }
            return;
        }
        if (gb.Bit() != 0)
        {
            int len = (int)gb.Read(3);
            Span<bool> seen = stackalloc bool[16];
            for (int i = 0; i <= len; i++)
            {
                syms[i] = (byte)gb.Read(4);
                seen[syms[i]] = true;
            }
            for (int i = 0; i < 16 && len < 15; i++)
            {
                if (!seen[i])
                {
                    syms[++len] = (byte)i;
                }
            }
        }
        else
        {
            int len = (int)gb.Read(2);
            byte[] input = new byte[16];
            byte[] output = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                input[i] = (byte)i;
            }
            for (int i = 0; i <= len; i++)
            {
                int size = 1 << i;
                for (int t = 0; t < 16; t += size << 1)
                {
                    Merge(gb, output, t, input, t, size);
                }
                (input, output) = (output, input);
            }
            Array.Copy(input, syms, 16);
        }
    }

    private static void Merge(LsbReader gb, byte[] dst, int d, byte[] src, int s, int size)
    {
        int s2 = s + size;
        int size2 = size;
        do
        {
            if (gb.Bit() == 0)
            {
                dst[d++] = src[s++];
                size--;
            }
            else
            {
                dst[d++] = src[s2++];
                size2--;
            }
        }
        while (size > 0 && size2 > 0);
        while (size-- > 0)
        {
            dst[d++] = src[s++];
        }
        while (size2-- > 0)
        {
            dst[d++] = src[s2++];
        }
    }

    private void ReadBundle(LsbReader gb, int n)
    {
        if (n == SrcColors)
        {
            for (int i = 0; i < 16; i++)
            {
                ReadTree(gb, ref _colHigh[i].Vlc, _colHigh[i].Syms);
            }
            _colLastVal = 0;
        }
        Bundle b = _bundles[n];
        if (n is not (SrcIntraDc or SrcInterDc))
        {
            ReadTree(gb, ref b.Vlc, b.Syms);
        }
        b.CurDec = b.CurPtr = 0;
    }

    private static bool Count(LsbReader gb, Bundle b, out int t)
    {
        t = 0;
        if (b.CurDec < 0 || b.CurDec > b.CurPtr)
        {
            return false;
        }
        t = (int)gb.Read(b.Len);
        if (t == 0)
        {
            b.CurDec = -1;
            return false;
        }
        return true;
    }

    private static void Room(Bundle b, int t, int capacity)
    {
        if (b.CurDec + t > capacity)
        {
            throw new InvalidDataException("a Bink bundle overflowed");
        }
    }

    private static void ReadRuns(LsbReader gb, Bundle b)
    {
        if (!Count(gb, b, out int t))
        {
            return;
        }
        Room(b, t, b.Bytes.Length);
        if (gb.Bit() != 0)
        {
            Array.Fill(b.Bytes, (byte)gb.Read(4), b.CurDec, t);
            b.CurDec += t;
        }
        else
        {
            for (int end = b.CurDec + t; b.CurDec < end;)
            {
                b.Bytes[b.CurDec++] = (byte)Huff(gb, b.Vlc, b.Syms);
            }
        }
    }

    private static void ReadMotion(LsbReader gb, Bundle b)
    {
        if (!Count(gb, b, out int t))
        {
            return;
        }
        Room(b, t, b.Bytes.Length);
        if (gb.Bit() != 0)
        {
            int v = (int)gb.Read(4);
            if (v != 0 && gb.Bit() != 0)
            {
                v = -v;
            }
            Array.Fill(b.Bytes, (byte)v, b.CurDec, t);
            b.CurDec += t;
        }
        else
        {
            for (int end = b.CurDec + t; b.CurDec < end;)
            {
                int v = Huff(gb, b.Vlc, b.Syms);
                if (v != 0 && gb.Bit() != 0)
                {
                    v = -v;
                }
                b.Bytes[b.CurDec++] = (byte)v;
            }
        }
    }

    private void ReadBlockTypes(LsbReader gb, Bundle b)
    {
        if (!Count(gb, b, out int t))
        {
            return;
        }
        if (_file.Revision == 'k')
        {
            t ^= 0xBB;
            if (t == 0)
            {
                b.CurDec = -1;
                return;
            }
        }
        Room(b, t, b.Bytes.Length);
        int end = b.CurDec + t;
        if (gb.Bit() != 0)
        {
            Array.Fill(b.Bytes, (byte)gb.Read(4), b.CurDec, t);
            b.CurDec += t;
            return;
        }
        int last = 0;
        while (b.CurDec < end)
        {
            int v = Huff(gb, b.Vlc, b.Syms);
            if (v < 12)
            {
                last = v;
                b.Bytes[b.CurDec++] = (byte)v;
            }
            else
            {
                int run = s_rleLens[v - 12];
                if (end - b.CurDec < run)
                {
                    throw new InvalidDataException("a Bink block type run overflowed");
                }
                Array.Fill(b.Bytes, (byte)last, b.CurDec, run);
                b.CurDec += run;
            }
        }
    }

    private static void ReadPatterns(LsbReader gb, Bundle b)
    {
        if (!Count(gb, b, out int t))
        {
            return;
        }
        Room(b, t, b.Bytes.Length);
        for (int end = b.CurDec + t; b.CurDec < end;)
        {
            int v = Huff(gb, b.Vlc, b.Syms);
            v |= Huff(gb, b.Vlc, b.Syms) << 4;
            b.Bytes[b.CurDec++] = (byte)v;
        }
    }

    private int Color(LsbReader gb, Bundle b)
    {
        _colLastVal = Huff(gb, _colHigh[_colLastVal].Vlc, _colHigh[_colLastVal].Syms);
        int v = (_colLastVal << 4) | Huff(gb, b.Vlc, b.Syms);
        if (_file.Revision < 'i')
        {
            int sign = (sbyte)v >> 7;
            v = (((v & 0x7F) ^ sign) - sign) + 0x80;
        }
        return v;
    }

    private void ReadColors(LsbReader gb, Bundle b)
    {
        if (!Count(gb, b, out int t))
        {
            return;
        }
        Room(b, t, b.Bytes.Length);
        if (gb.Bit() != 0)
        {
            Array.Fill(b.Bytes, (byte)Color(gb, b), b.CurDec, t);
            b.CurDec += t;
        }
        else
        {
            for (int end = b.CurDec + t; b.CurDec < end;)
            {
                b.Bytes[b.CurDec++] = (byte)Color(gb, b);
            }
        }
    }

    private static void ReadDcs(LsbReader gb, Bundle b, bool signed)
    {
        if (!Count(gb, b, out int len))
        {
            return;
        }
        short[] dst = b.Shorts;
        int v = (int)gb.Read(11 - (signed ? 1 : 0));
        if (v != 0 && signed && gb.Bit() != 0)
        {
            v = -v;
        }
        Room(b, len, dst.Length);
        dst[b.CurDec++] = (short)v;
        len--;
        for (int i = 0; i < len; i += 8)
        {
            int len2 = Math.Min(len - i, 8);
            int bsize = (int)gb.Read(4);
            for (int j = 0; j < len2; j++)
            {
                if (bsize != 0)
                {
                    int v2 = (int)gb.Read(bsize);
                    if (v2 != 0 && gb.Bit() != 0)
                    {
                        v2 = -v2;
                    }
                    v += v2;
                    if (v is < -32768 or > 32767)
                    {
                        throw new InvalidDataException("a Bink DC value went out of range");
                    }
                }
                dst[b.CurDec++] = (short)v;
            }
        }
    }

    private int Value(int n)
    {
        Bundle b = _bundles[n];
        return n switch
        {
            SrcXOff or SrcYOff => (sbyte)b.Bytes[b.CurPtr++],
            SrcIntraDc or SrcInterDc => b.Shorts[b.CurPtr++],
            _ => b.Bytes[b.CurPtr++],
        };
    }

    private int DctCoeffs(LsbReader gb, int[] block, out int count)
    {
        Span<int> coefList = stackalloc int[128];
        Span<int> modeList = stackalloc int[128];
        int listStart = 64;
        int listEnd = 64;
        count = 0;
        byte[] scan = BinkTables.Scan;
        coefList[listEnd] = 4;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 24;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 44;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 1;
        modeList[listEnd++] = 3;
        coefList[listEnd] = 2;
        modeList[listEnd++] = 3;
        coefList[listEnd] = 3;
        modeList[listEnd++] = 3;
        for (int bits = (int)gb.Read(4) - 1; bits >= 0; bits--)
        {
            int listPos = listStart;
            while (listPos < listEnd)
            {
                if ((modeList[listPos] | coefList[listPos]) == 0 || gb.Bit() == 0)
                {
                    listPos++;
                    continue;
                }
                int ccoef = coefList[listPos];
                int mode = modeList[listPos];
                switch (mode)
                {
                    case 0:
                    case 2:
                        if (mode == 0)
                        {
                            coefList[listPos] = ccoef + 4;
                            modeList[listPos] = 1;
                        }
                        else
                        {
                            coefList[listPos] = 0;
                            modeList[listPos++] = 0;
                        }
                        for (int i = 0; i < 4; i++, ccoef++)
                        {
                            if (gb.Bit() != 0)
                            {
                                coefList[--listStart] = ccoef;
                                modeList[listStart] = 3;
                            }
                            else
                            {
                                block[scan[ccoef]] = Coefficient(gb, bits);
                                _coefIdx[count++] = ccoef;
                            }
                        }
                        break;
                    case 1:
                        modeList[listPos] = 2;
                        for (int i = 0; i < 3; i++)
                        {
                            ccoef += 4;
                            coefList[listEnd] = ccoef;
                            modeList[listEnd++] = 2;
                        }
                        break;
                    default:
                        block[scan[ccoef]] = Coefficient(gb, bits);
                        _coefIdx[count++] = ccoef;
                        coefList[listPos] = 0;
                        modeList[listPos++] = 0;
                        break;
                }
            }
        }
        return (int)gb.Read(4);
    }

    private static int Coefficient(LsbReader gb, int bits)
    {
        if (bits == 0)
        {
            return 1 - (gb.Bit() << 1);
        }
        int t = (int)gb.Read(bits) | (1 << bits);
        return gb.Bit() != 0 ? -t : t;
    }

    private void Unquantize(int[] block, int[] quant, int q, int count)
    {
        byte[] scan = BinkTables.Scan;
        block[0] = unchecked((int)((uint)block[0] * (uint)quant[q * 64])) >> 11;
        for (int i = 0; i < count; i++)
        {
            int idx = _coefIdx[i];
            block[scan[idx]] = unchecked((int)((uint)block[scan[idx]] * (uint)quant[(q * 64) + idx])) >> 11;
        }
    }

    private static void ReadResidue(LsbReader gb, short[] block, int masksCount)
    {
        Span<int> coefList = stackalloc int[128];
        Span<int> modeList = stackalloc int[128];
        Span<int> nz = stackalloc int[64];
        int nzCount = 0;
        int listStart = 64;
        int listEnd = 64;
        byte[] scan = BinkTables.Scan;
        coefList[listEnd] = 4;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 24;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 44;
        modeList[listEnd++] = 0;
        coefList[listEnd] = 0;
        modeList[listEnd++] = 2;
        for (int mask = 1 << (int)gb.Read(3); mask != 0; mask >>= 1)
        {
            for (int i = 0; i < nzCount; i++)
            {
                if (gb.Bit() == 0)
                {
                    continue;
                }
                block[nz[i]] = (short)(block[nz[i]] < 0 ? block[nz[i]] - mask : block[nz[i]] + mask);
                if (--masksCount < 0)
                {
                    return;
                }
            }
            int listPos = listStart;
            while (listPos < listEnd)
            {
                if ((coefList[listPos] | modeList[listPos]) == 0 || gb.Bit() == 0)
                {
                    listPos++;
                    continue;
                }
                int ccoef = coefList[listPos];
                int mode = modeList[listPos];
                switch (mode)
                {
                    case 0:
                    case 2:
                        if (mode == 0)
                        {
                            coefList[listPos] = ccoef + 4;
                            modeList[listPos] = 1;
                        }
                        else
                        {
                            coefList[listPos] = 0;
                            modeList[listPos++] = 0;
                        }
                        for (int i = 0; i < 4; i++, ccoef++)
                        {
                            if (gb.Bit() != 0)
                            {
                                coefList[--listStart] = ccoef;
                                modeList[listStart] = 3;
                            }
                            else
                            {
                                nz[nzCount++] = scan[ccoef];
                                block[scan[ccoef]] = (short)(gb.Bit() != 0 ? -mask : mask);
                                if (--masksCount < 0)
                                {
                                    return;
                                }
                            }
                        }
                        break;
                    case 1:
                        modeList[listPos] = 2;
                        for (int i = 0; i < 3; i++)
                        {
                            ccoef += 4;
                            coefList[listEnd] = ccoef;
                            modeList[listEnd++] = 2;
                        }
                        break;
                    default:
                        nz[nzCount++] = scan[ccoef];
                        block[scan[ccoef]] = (short)(gb.Bit() != 0 ? -mask : mask);
                        coefList[listPos] = 0;
                        modeList[listPos++] = 0;
                        if (--masksCount < 0)
                        {
                            return;
                        }
                        break;
                }
            }
        }
    }

    private static int Mul(int x, int y)
    {
        return unchecked((int)((uint)x * (uint)y)) >> 11;
    }

    private static void Transform(int[] d, int d0, int step, int[] s, int s0, int sstep, bool row)
    {
        int x0 = s[s0];
        int x1 = s[s0 + sstep];
        int x2 = s[s0 + (2 * sstep)];
        int x3 = s[s0 + (3 * sstep)];
        int x4 = s[s0 + (4 * sstep)];
        int x5 = s[s0 + (5 * sstep)];
        int x6 = s[s0 + (6 * sstep)];
        int x7 = s[s0 + (7 * sstep)];
        int a0 = x0 + x4;
        int a1 = x0 - x4;
        int a2 = x2 + x6;
        int a3 = Mul(2896, x2 - x6);
        int a4 = x5 + x3;
        int a5 = x5 - x3;
        int a6 = x1 + x7;
        int a7 = x1 - x7;
        int b0 = a4 + a6;
        int b1 = Mul(3784, a5 + a7);
        int b2 = Mul(-5352, a5) - b0 + b1;
        int b3 = Mul(2896, a6 - a4) - b2;
        int b4 = Mul(2217, a7) + b3 - b1;
        int r = row ? 0x7F : 0;
        int sh = row ? 8 : 0;
        d[d0] = (a0 + a2 + b0 + r) >> sh;
        d[d0 + step] = (a1 + a3 - a2 + b2 + r) >> sh;
        d[d0 + (2 * step)] = (a1 - a3 + a2 + b3 + r) >> sh;
        d[d0 + (3 * step)] = (a0 - a2 - b4 + r) >> sh;
        d[d0 + (4 * step)] = (a0 - a2 + b4 + r) >> sh;
        d[d0 + (5 * step)] = (a1 - a3 + a2 - b3 + r) >> sh;
        d[d0 + (6 * step)] = (a1 + a3 - a2 - b2 + r) >> sh;
        d[d0 + (7 * step)] = (a0 + a2 - b0 + r) >> sh;
    }

    private void Idct(int[] block)
    {
        for (int i = 0; i < 8; i++)
        {
            if ((block[i + 8] | block[i + 16] | block[i + 24] | block[i + 32] | block[i + 40] | block[i + 48] | block[i + 56]) == 0)
            {
                for (int k = 0; k < 8; k++)
                {
                    _temp[i + (8 * k)] = block[i];
                }
            }
            else
            {
                Transform(_temp, i, 8, block, i, 8, false);
            }
        }
        for (int i = 0; i < 8; i++)
        {
            Transform(block, 8 * i, 1, _temp, 8 * i, 1, true);
        }
    }

    private void IdctPut(byte[] dst, int at, int stride, int[] block)
    {
        Idct(block);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                dst[at + (y * stride) + x] = unchecked((byte)block[(y * 8) + x]);
            }
        }
    }

    private void IdctAdd(byte[] dst, int at, int stride, int[] block)
    {
        Idct(block);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                dst[at + (y * stride) + x] = unchecked((byte)(dst[at + (y * stride) + x] + block[(y * 8) + x]));
            }
        }
    }

    private static void Copy8(byte[] dst, int at, byte[] src, int from, int stride)
    {
        for (int y = 0; y < 8; y++)
        {
            Buffer.BlockCopy(src, from + (y * stride), dst, at + (y * stride), 8);
        }
    }

    private static void Fill(byte[] dst, int at, int stride, int size, byte v)
    {
        for (int y = 0; y < size; y++)
        {
            dst.AsSpan(at + (y * stride), size).Fill(v);
        }
    }

    private void Motion(byte[] dst, int at, byte[] prev, int stride, int bw, int bh)
    {
        int xoff = Value(SrcXOff);
        int yoff = Value(SrcYOff);
        int from = at + xoff + (yoff * stride);
        if (from < 0 || from > ((bw - 1) * 8) + ((bh - 1) * 8 * stride))
        {
            throw new InvalidDataException($"a Bink motion vector points outside the frame ({xoff}, {yoff})");
        }
        Copy8(dst, at, prev, from, stride);
    }

    private void Plane(LsbReader gb, int plane, bool chroma)
    {
        int width = _file.Width >> (chroma ? 1 : 0);
        int height = _file.Height >> (chroma ? 1 : 0);
        int bw = chroma ? (_file.Width + 15) >> 4 : (_file.Width + 7) >> 3;
        int bh = chroma ? (_file.Height + 15) >> 4 : (_file.Height + 7) >> 3;
        int stride = _strides[plane];
        byte[] dst = _cur[plane];
        byte[] prev = _haveLast ? _last[plane] : _cur[plane];
        if (_file.Revision == 'k' && gb.Bit() != 0)
        {
            byte fill = (byte)gb.Read(8);
            for (int y = 0; y < height; y++)
            {
                dst.AsSpan(y * stride, width).Fill(fill);
            }
            Align(gb);
            return;
        }
        int lw = (Math.Max(width, 8) + 7) & ~7;
        int log(int v) => System.Numerics.BitOperations.Log2((uint)v);
        _bundles[SrcBlockTypes].Len = log((lw >> 3) + 511) + 1;
        _bundles[SrcSubBlockTypes].Len = log((lw >> 4) + 511) + 1;
        _bundles[SrcColors].Len = log((bw * 64) + 511) + 1;
        _bundles[SrcIntraDc].Len = _bundles[SrcInterDc].Len = _bundles[SrcXOff].Len = _bundles[SrcYOff].Len = log((lw >> 3) + 511) + 1;
        _bundles[SrcPattern].Len = log((bw << 3) + 511) + 1;
        _bundles[SrcRun].Len = log((bw * 48) + 511) + 1;
        for (int i = 0; i < Sources; i++)
        {
            ReadBundle(gb, i);
        }
        Span<int> coordmap = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            coordmap[i] = (i & 7) + ((i >> 3) * stride);
        }
        for (int by = 0; by < bh; by++)
        {
            ReadBlockTypes(gb, _bundles[SrcBlockTypes]);
            ReadBlockTypes(gb, _bundles[SrcSubBlockTypes]);
            ReadColors(gb, _bundles[SrcColors]);
            ReadPatterns(gb, _bundles[SrcPattern]);
            ReadMotion(gb, _bundles[SrcXOff]);
            ReadMotion(gb, _bundles[SrcYOff]);
            ReadDcs(gb, _bundles[SrcIntraDc], false);
            ReadDcs(gb, _bundles[SrcInterDc], true);
            ReadRuns(gb, _bundles[SrcRun]);
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (8 * by * stride) + (8 * bx);
                int blk = Value(SrcBlockTypes);
                if (((by & 1) != 0 || (bx & 1) != 0) && blk == 1)
                {
                    bx++;
                    continue;
                }
                switch (blk)
                {
                    case 0:
                        Copy8(dst, at, prev, at, stride);
                        break;
                    case 1:
                        Scaled(gb, dst, at, stride);
                        bx++;
                        break;
                    case 2:
                        Motion(dst, at, prev, stride, bw, bh);
                        break;
                    case 3:
                        Run(gb, dst, at, coordmap);
                        break;
                    case 4:
                        Motion(dst, at, prev, stride, bw, bh);
                        Array.Clear(_residue);
                        ReadResidue(gb, _residue, (int)gb.Read(7));
                        for (int y = 0; y < 8; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                dst[at + (y * stride) + x] = unchecked((byte)(dst[at + (y * stride) + x] + _residue[(y * 8) + x]));
                            }
                        }
                        break;
                    case 5:
                        Array.Clear(_dct);
                        _dct[0] = Value(SrcIntraDc);
                        Unquantize(_dct, BinkTables.IntraQuant, Quant(gb), _lastCount);
                        IdctPut(dst, at, stride, _dct);
                        break;
                    case 6:
                        Fill(dst, at, stride, 8, (byte)Value(SrcColors));
                        break;
                    case 7:
                        Motion(dst, at, prev, stride, bw, bh);
                        Array.Clear(_dct);
                        _dct[0] = Value(SrcInterDc);
                        Unquantize(_dct, BinkTables.InterQuant, Quant(gb), _lastCount);
                        IdctAdd(dst, at, stride, _dct);
                        break;
                    case 8:
                    {
                        int c0 = Value(SrcColors);
                        int c1 = Value(SrcColors);
                        for (int y = 0; y < 8; y++)
                        {
                            int v = Value(SrcPattern);
                            for (int x = 0; x < 8; x++, v >>= 1)
                            {
                                dst[at + (y * stride) + x] = (byte)((v & 1) != 0 ? c1 : c0);
                            }
                        }
                        break;
                    }
                    case 9:
                    {
                        Bundle colors = _bundles[SrcColors];
                        for (int y = 0; y < 8; y++)
                        {
                            Buffer.BlockCopy(colors.Bytes, colors.CurPtr + (y * 8), dst, at + (y * stride), 8);
                        }
                        colors.CurPtr += 64;
                        break;
                    }
                    default:
                        throw new InvalidDataException($"unknown Bink block type {blk}");
                }
            }
        }
        Align(gb);
    }

    private int _lastCount;

    private int Quant(LsbReader gb)
    {
        int q = DctCoeffs(gb, _dct, out _lastCount);
        return q > 15 ? throw new InvalidDataException($"Bink quantizer {q} out of range") : q;
    }

    private static void Align(LsbReader gb)
    {
        if ((gb.Pos & 31) != 0)
        {
            gb.Pos += 32 - (gb.Pos & 31);
        }
    }

    private void Run(LsbReader gb, byte[] dst, int at, ReadOnlySpan<int> coordmap)
    {
        int pattern = (int)gb.Read(4) * 64;
        int i = 0;
        int s = 0;
        do
        {
            int run = Value(SrcRun) + 1;
            i += run;
            if (i > 64)
            {
                throw new InvalidDataException("a Bink run went out of bounds");
            }
            if (gb.Bit() != 0)
            {
                byte v = (byte)Value(SrcColors);
                for (int j = 0; j < run; j++)
                {
                    dst[at + coordmap[BinkTables.Patterns[pattern + s++]]] = v;
                }
            }
            else
            {
                for (int j = 0; j < run; j++)
                {
                    dst[at + coordmap[BinkTables.Patterns[pattern + s++]]] = (byte)Value(SrcColors);
                }
            }
        }
        while (i < 63);
        if (i == 63)
        {
            dst[at + coordmap[BinkTables.Patterns[pattern + s]]] = (byte)Value(SrcColors);
        }
    }

    private void Scaled(LsbReader gb, byte[] dst, int at, int stride)
    {
        int blk = Value(SrcSubBlockTypes);
        byte[] u = _ublock;
        switch (blk)
        {
            case 3:
            {
                if (gb.Left < 4)
                {
                    throw new InvalidDataException("a Bink run block runs past the frame");
                }
                int pattern = (int)gb.Read(4) * 64;
                int i = 0;
                int s = 0;
                do
                {
                    int run = Value(SrcRun) + 1;
                    i += run;
                    if (i > 64)
                    {
                        throw new InvalidDataException("a Bink run went out of bounds");
                    }
                    if (gb.Bit() != 0)
                    {
                        byte v = (byte)Value(SrcColors);
                        for (int j = 0; j < run; j++)
                        {
                            u[BinkTables.Patterns[pattern + s++]] = v;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < run; j++)
                        {
                            u[BinkTables.Patterns[pattern + s++]] = (byte)Value(SrcColors);
                        }
                    }
                }
                while (i < 63);
                if (i == 63)
                {
                    u[BinkTables.Patterns[pattern + s]] = (byte)Value(SrcColors);
                }
                break;
            }
            case 5:
                Array.Clear(_dct);
                _dct[0] = Value(SrcIntraDc);
                Unquantize(_dct, BinkTables.IntraQuant, Quant(gb), _lastCount);
                IdctPut(u, 0, 8, _dct);
                break;
            case 6:
                Fill(dst, at, stride, 16, (byte)Value(SrcColors));
                return;
            case 8:
            {
                int c0 = Value(SrcColors);
                int c1 = Value(SrcColors);
                for (int j = 0; j < 8; j++)
                {
                    int v = Value(SrcPattern);
                    for (int i = 0; i < 8; i++, v >>= 1)
                    {
                        u[i + (j * 8)] = (byte)((v & 1) != 0 ? c1 : c0);
                    }
                }
                break;
            }
            case 9:
                for (int k = 0; k < 64; k++)
                {
                    u[k] = (byte)Value(SrcColors);
                }
                break;
            default:
                throw new InvalidDataException($"incorrect Bink 16x16 block type {blk}");
        }
        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                byte v = u[(j * 8) + i];
                int row = at + (2 * j * stride) + (2 * i);
                dst[row] = dst[row + 1] = dst[row + stride] = dst[row + stride + 1] = v;
            }
        }
    }

    public void Decode(int frame)
    {
        (int offset, int length) = _file.Video(frame);
        LsbReader gb = new(_file.Data, offset, length);
        int bits = length * 8;
        bool padded = _file.Revision >= 'i';
        if (_file.HasAlpha)
        {
            gb.Pos += padded ? 32 : 0;
            Plane(gb, 3, false);
        }
        gb.Pos += padded ? 32 : 0;
        for (int plane = 0; plane < 3; plane++)
        {
            int index = plane == 0 || _file.Revision < 'h' ? plane : plane ^ 3;
            Plane(gb, index, plane != 0);
            if (gb.Pos >= bits)
            {
                break;
            }
        }
        (_cur, _last) = (_last, _cur);
        _haveLast = true;
    }
}

internal static class BinkColor
{
    private static readonly int[] s_y = [.. Enumerable.Range(0, 256).Select(y => (int)Math.Round((y - 16) * 1.164383 * 65536))];
    private static readonly int[] s_rv = [.. Enumerable.Range(0, 256).Select(v => (int)Math.Round((v - 128) * 1.596027 * 65536))];
    private static readonly int[] s_gu = [.. Enumerable.Range(0, 256).Select(u => (int)Math.Round((u - 128) * -0.391762 * 65536))];
    private static readonly int[] s_gv = [.. Enumerable.Range(0, 256).Select(v => (int)Math.Round((v - 128) * -0.812968 * 65536))];
    private static readonly int[] s_bu = [.. Enumerable.Range(0, 256).Select(u => (int)Math.Round((u - 128) * 2.017232 * 65536))];

    private static byte Clip(int v)
    {
        v = (v + 32768) >> 16;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private static void Upsample(byte[] c, int stride, int cw, int ch, int y, Span<int> row, Span<int> output)
    {
        int cy = y >> 1;
        int ny = Math.Clamp((y & 1) != 0 ? cy + 1 : cy - 1, 0, ch - 1);
        int near = cy * stride;
        int far = ny * stride;
        for (int cx = 0; cx < cw; cx++)
        {
            row[cx] = (3 * c[near + cx]) + c[far + cx];
        }
        for (int x = 0; x < output.Length; x++)
        {
            int cx = x >> 1;
            int nx = Math.Clamp((x & 1) != 0 ? cx + 1 : cx - 1, 0, cw - 1);
            output[x] = ((3 * row[cx]) + row[nx] + 8) >> 4;
        }
    }

    public static void ToRgba(BinkFile file, byte[][] planes, int[] strides, byte[] rgba)
    {
        int w = file.Width;
        int h = file.Height;
        int cw = (w + 1) >> 1;
        int chh = (h + 1) >> 1;
        byte[] yp = planes[0];
        byte[] up = planes[1];
        byte[] vp = planes[2];
        byte[]? ap = planes.Length > 3 ? planes[3] : null;
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, h / 16));
        Parallel.For(0, bands, band =>
        {
            int[] row = new int[cw];
            int[] us = new int[w];
            int[] vs = new int[w];
            for (int y = band * h / bands; y < (band + 1) * h / bands; y++)
            {
                Upsample(up, strides[1], cw, chh, y, row, us);
                Upsample(vp, strides[2], cw, chh, y, row, vs);
                int lumaRow = y * strides[0];
                int o = y * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    int luma = s_y[yp[lumaRow + x]];
                    int u = us[x];
                    int v = vs[x];
                    rgba[o] = Clip(luma + s_rv[v]);
                    rgba[o + 1] = Clip(luma + s_gu[u] + s_gv[v]);
                    rgba[o + 2] = Clip(luma + s_bu[u]);
                    rgba[o + 3] = ap is null ? (byte)255 : ap[(y * strides[3]) + x];
                }
            }
        });
    }
}

internal sealed class BinkReader : Omega.Plugins.IVideoReader
{
    private readonly BinkFile _file;
    private readonly BinkVideo _video;

    public BinkReader(BinkFile file)
    {
        _file = file;
        _video = new BinkVideo(file);
        Info = new Omega.Plugins.VideoInfo(file.Width, file.Height, file.RateNumerator, file.RateDenominator, file.Frames, $"Bink {file.Revision}");
    }

    public Omega.Plugins.VideoInfo Info { get; }
    public int Position { get; private set; }
    private int _decoded = -1;

    public void Seek(int frame)
    {
        Position = Math.Clamp(frame, 0, _file.Frames);
    }

    private byte[] _rgba = [];

    public bool ReadFrame(Span<byte> rgba)
    {
        if (Position >= _file.Frames)
        {
            return false;
        }
        if (_rgba.Length != Info.FrameBytes)
        {
            _rgba = new byte[Info.FrameBytes];
        }
        int key = Math.Max(0, Array.FindLastIndex(_file.Keyframes, Position, k => k));
        int start = _decoded >= key && _decoded < Position ? _decoded + 1 : key;
        for (int f = start; f <= Position; f++)
        {
            _video.Decode(f);
            _decoded = f;
        }
        BinkColor.ToRgba(_file, _video.Frame, _video.Strides, _rgba);
        _rgba.CopyTo(rgba);
        Position++;
        return true;
    }

    public void Dispose()
    {
    }
}

internal static class Bink
{
    public static (short[] Samples, int Rate, int Channels)? Audio(BinkFile file, int track = 0)
    {
        if (file.Tracks.Count <= track)
        {
            return null;
        }
        BinkAudio decoder = new(file.Tracks[track]);
        List<short> output = [];
        for (int f = 0; f < file.Frames; f++)
        {
            (int offset, int length) = file.Audio(f, track);
            if (length >= 4)
            {
                decoder.Packet(file.Data, offset, length, output);
            }
        }
        return ([.. output], file.Tracks[track].Rate, decoder.Channels);
    }
}
